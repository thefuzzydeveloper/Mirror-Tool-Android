import os
import sys
import re
import json
import socket
import struct
import ctypes
import queue
import hashlib
import winreg
import threading
import subprocess
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from socketserver import ThreadingMixIn
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path, PurePosixPath
from typing import Optional, Dict, Any, List, Tuple, Set

from PIL import Image, ImageDraw
import pystray
from pystray import MenuItem as item, Menu
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from watchdog.observers import Observer
from watchdog.events import (
    FileSystemEventHandler,
    FileCreatedEvent,
    FileModifiedEvent,
    FileDeletedEvent,
    FileMovedEvent,
    DirCreatedEvent,
    DirDeletedEvent,
    DirMovedEvent,
    FileSystemEvent,
)

# --- Constants & Protocol Framing ---
CHUNK_STREAM_SIZE = 4 * 1024 * 1024  # 4MB streaming chunks
APP_NAME = "WiFiAutoStreamSync"
CONFIG_FILE = Path.home() / f".{APP_NAME.lower()}_config.json"
REG_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
MUTEX_NAME = f"Global\\{APP_NAME}_SingleInstance_Mutex"
ERROR_ALREADY_EXISTS = 183

MAGIC_HEADER = b"\xAA\x55"
TCP_DATA_PORT = 58421
HTTP_MANIFEST_PORT = 58422
UDP_BEACON_PORT = 58423

CMD_PING = 0x00
CMD_CONFIG = 0x01
CMD_MANIFEST_EXCHANGE = 0x02
CMD_FILE_STREAM = 0x03
CMD_DELETE = 0x04
CMD_SYNC_END = 0x05


def ensure_firewall_rule():
    """Attempts to allow ports through Windows Firewall to prevent inbound timeouts."""
    try:
        cmd = (
            f'netsh advfirewall firewall add rule name="{APP_NAME}" '
            f'dir=in action=allow protocol=TCP localport={TCP_DATA_PORT},{HTTP_MANIFEST_PORT}'
        )
        subprocess.run(cmd, shell=True, capture_output=True, check=False)
    except Exception:
        pass


def recv_exact(sock: socket.socket, num_bytes: int) -> bytes:
    buf = bytearray()
    while len(buf) < num_bytes:
        chunk = sock.recv(num_bytes - len(buf))
        if not chunk:
            raise ConnectionError("Socket disconnected unexpectedly while reading.")
        buf.extend(chunk)
    return bytes(buf)


class SingleInstanceGuard:
    def __init__(self, mutex_name: str = MUTEX_NAME):
        self.mutex_name = mutex_name
        self.mutex = ctypes.windll.kernel32.CreateMutexW(None, False, self.mutex_name)
        self.already_running = (ctypes.windll.kernel32.GetLastError() == ERROR_ALREADY_EXISTS)

    def is_running(self) -> bool:
        return self.already_running

    def release(self) -> None:
        if self.mutex:
            ctypes.windll.kernel32.CloseHandle(self.mutex)
            self.mutex = None


class WindowsStartup:
    @staticmethod
    def is_enabled() -> bool:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, REG_RUN_KEY, 0, winreg.KEY_READ) as key:
                winreg.QueryValueEx(key, APP_NAME)
                return True
        except (FileNotFoundError, OSError):
            return False

    @staticmethod
    def toggle(enable: bool) -> None:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, REG_RUN_KEY, 0, winreg.KEY_ALL_ACCESS) as key:
                if enable:
                    cmd = f'"{sys.executable}"' if getattr(sys, 'frozen', False) else f'"{sys.executable}" "{os.path.abspath(__file__)}"'
                    winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, cmd)
                else:
                    try:
                        winreg.DeleteValue(key, APP_NAME)
                    except FileNotFoundError:
                        pass
        except Exception as e:
            sys.stderr.write(f"[Registry Error] {e}\n")


def create_dynamic_icon(syncing: bool = False) -> Image.Image:
    size = (64, 64)
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    bg_color = (30, 41, 59, 255)
    draw.ellipse([(2, 2), (62, 62)], fill=bg_color)

    primary_color = (56, 189, 248) if not syncing else (74, 222, 128)
    draw.arc([(12, 12), (52, 52)], start=30, end=150, fill=primary_color, width=4)
    draw.arc([(12, 12), (52, 52)], start=210, end=330, fill=primary_color, width=4)

    draw.polygon([(46, 20), (54, 28), (42, 30)], fill=primary_color)
    draw.polygon([(18, 44), (10, 36), (22, 34)], fill=primary_color)

    center_color = (250, 204, 21) if syncing else (148, 163, 184)
    draw.ellipse([(28, 28), (36, 36)], fill=center_color)
    return image


class SafeTrayIconManager:
    def __init__(self):
        self._icon: Optional[pystray.Icon] = None
        self._lock = threading.Lock()
        self._active_transfers = 0

    def set_icon(self, icon: pystray.Icon):
        self._icon = icon

    def start_transfer(self, filename: str):
        with self._lock:
            self._active_transfers += 1
            self._update_ui(syncing=True, status_text=f"Broadcasting: {filename}")

    def stop_transfer(self):
        with self._lock:
            self._active_transfers = max(0, self._active_transfers - 1)
            if self._active_transfers == 0:
                self._update_ui(syncing=False, status_text="Sync Completed")

    def set_status(self, text: str):
        with self._lock:
            self._update_ui(syncing=(self._active_transfers > 0), status_text=text)

    def _update_ui(self, syncing: bool, status_text: str):
        if not self._icon:
            return
        try:
            truncated = status_text if len(status_text) <= 120 else status_text[:117] + "..."
            self._icon.title = f"Wi-Fi Sync | {truncated}"
            self._icon.icon = create_dynamic_icon(syncing=syncing)
        except Exception:
            pass


def normalize_extensions(ext_input: Any) -> List[str]:
    if isinstance(ext_input, str):
        parts = ext_input.replace(";", ",").split(",")
    elif isinstance(ext_input, list):
        parts = ext_input
    else:
        return ["*"]

    cleaned = []
    for p in parts:
        s = str(p).strip().lower()
        if s:
            if s == "*":
                return ["*"]
            if not s.startswith("."):
                s = f".{s}"
            cleaned.append(s)
    return cleaned if cleaned else ["*"]


def is_extension_allowed(file_path: Path, allowed_exts: List[str]) -> bool:
    if not allowed_exts or "*" in allowed_exts or ".*" in allowed_exts:
        return True
    return file_path.suffix.lower() in set(allowed_exts)


def compute_target_rel_path(rel_path: Path, scrub_level: int) -> PurePosixPath:
    parts = rel_path.parts
    if scrub_level <= 0 or len(parts) <= scrub_level + 1:
        return PurePosixPath(*parts)

    top_dirs = parts[:scrub_level]
    flattened_filename = "_".join(parts[scrub_level:])
    return PurePosixPath(*top_dirs) / flattened_filename


def get_folder_id(folder_path: str) -> str:
    return hashlib.md5(folder_path.lower().encode("utf-8")).hexdigest()[:10]


class ConfigManager:
    DEFAULT_CONFIG = {
        "manual_ip": "",
        "windows_folders": [
            {
                "path": str(Path.home() / "SyncWorkspace"),
                "extensions": ["*"],
                "scrub_level": 0
            }
        ]
    }

    @classmethod
    def load(cls) -> Dict[str, Any]:
        if CONFIG_FILE.exists():
            try:
                with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                    data = json.load(f)
                    folders = data.get("windows_folders", [])
                    normalized = []
                    for item_data in folders:
                        if isinstance(item_data, dict):
                            normalized.append({
                                "path": item_data.get("path", ""),
                                "extensions": normalize_extensions(item_data.get("extensions", ["*"])),
                                "scrub_level": int(item_data.get("scrub_level", 0))
                            })
                    data["windows_folders"] = normalized
                    return {**cls.DEFAULT_CONFIG, **data}
            except Exception:
                return cls.DEFAULT_CONFIG.copy()
        return cls.DEFAULT_CONFIG.copy()

    @classmethod
    def save(cls, data: Dict[str, Any]) -> None:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=4)


class NetworkDiscovery:
    @staticmethod
    def get_active_ipv4_subnets() -> List[str]:
        ips = []
        try:
            res = subprocess.run(["ipconfig"], capture_output=True, text=True, check=False)
            matches = re.findall(r"IPv4 Address[.\s]+:\s*([0-9.]+)", res.stdout)
            for ip in matches:
                ip = ip.strip()
                if not ip.startswith("127.") and not ip.startswith("169.254"):
                    ips.append(ip)
        except Exception:
            pass

        if not ips:
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                s.connect(("8.8.8.8", 80))
                ips.append(s.getsockname()[0])
                s.close()
            except Exception:
                pass

        return list(set(ips))

    @classmethod
    def find_all_devices(cls, manual_ip_str: str = "", exclude_ips: Set[str] = None) -> Set[str]:
        found_devices: Set[str] = set()
        exclude = exclude_ips or set()

        if manual_ip_str and manual_ip_str.strip():
            raw_ips = [ip.strip() for ip in manual_ip_str.replace(";", ",").split(",") if ip.strip()]
            for ip in raw_ips:
                if ip not in exclude and cls._test_ip(ip):
                    found_devices.add(ip)

        local_ips = cls.get_active_ipv4_subnets()
        candidate_ips: Set[str] = set()

        for local_ip in local_ips:
            parts = local_ip.split(".")
            if len(parts) == 4:
                subnet_prefix = f"{parts[0]}.{parts[1]}.{parts[2]}"
                for i in range(1, 255):
                    candidate_ip = f"{subnet_prefix}.{i}"
                    if candidate_ip not in exclude:
                        candidate_ips.add(candidate_ip)

        # Reduced worker pool and increased timeout to prevent router packet dropping
        with ThreadPoolExecutor(max_workers=40) as executor:
            futures = {executor.submit(cls._test_ip, ip): ip for ip in candidate_ips}
            for future in as_completed(futures):
                res = future.result()
                if res:
                    found_devices.add(res)

        return found_devices

    @staticmethod
    def _test_ip(ip: str) -> Optional[str]:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(0.7)  # Increased from 0.25 to prevent false negatives
        try:
            if s.connect_ex((ip, TCP_DATA_PORT)) == 0:
                s.sendall(MAGIC_HEADER + struct.pack("!B", CMD_PING))
                resp = s.recv(1)
                s.close()
                if resp == b"\x00":
                    return ip
        except Exception:
            pass
        finally:
            try:
                s.close()
            except Exception:
                pass
        return None


class DeviceClient:
    def __init__(self, ip: str, port: int = TCP_DATA_PORT):
        self.ip = ip
        self.port = port
        self.sock: Optional[socket.socket] = None
        self.lock = threading.Lock()
        self.is_connected = False

    def connect(self, timeout: float = 10.0) -> bool:
        with self.lock:
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                s.settimeout(timeout)
                s.connect((self.ip, self.port))
                self.sock = s
                self.is_connected = True
                return True
            except Exception:
                self._disconnect()
                return False

    def _disconnect(self):
        self.is_connected = False
        if self.sock:
            try:
                self.sock.close()
            except Exception:
                pass
            self.sock = None

    def close(self):
        with self.lock:
            self._disconnect()

    def send_config(self, windows_folders: List[Dict[str, Any]]) -> bool:
        with self.lock:
            if not self.is_connected or not self.sock:
                return False

            payload = []
            for folder_info in windows_folders:
                p = Path(folder_info["path"]).resolve()
                payload.append({
                    "id": get_folder_id(str(p)),
                    "name": p.name,
                    "local_path": str(p),
                    "extensions": folder_info.get("extensions", ["*"]),
                    "scrub_level": folder_info.get("scrub_level", 0)
                })

            json_bytes = json.dumps(payload, indent=2).encode("utf-8")
            try:
                packet = MAGIC_HEADER + struct.pack("!BI", CMD_CONFIG, len(json_bytes)) + json_bytes
                self.sock.sendall(packet)
                ack = recv_exact(self.sock, 1)
                return ack == b"\x00"
            except Exception:
                self._disconnect()
                return False

    def exchange_manifest(self, folder_id: str, win_manifest: Dict[str, int]) -> Optional[Dict[str, Any]]:
        with self.lock:
            if not self.is_connected or not self.sock:
                return None

            f_bytes = folder_id.encode("utf-8")
            payload = json.dumps({"files": win_manifest}).encode("utf-8")
            packet = (
                MAGIC_HEADER
                + struct.pack("!BH", CMD_MANIFEST_EXCHANGE, len(f_bytes))
                + f_bytes
                + struct.pack("!I", len(payload))
                + payload
            )

            try:
                self.sock.sendall(packet)
                len_bytes = recv_exact(self.sock, 4)
                resp_len = struct.unpack("!I", len_bytes)[0]
                resp_data = recv_exact(self.sock, resp_len)
                return json.loads(resp_data.decode("utf-8"))
            except Exception:
                self._disconnect()
                return None

    def stream_file(self, folder_id: str, local_file: Path, rel_target: PurePosixPath) -> bool:
        with self.lock:
            if not self.is_connected or not self.sock:
                return False

            f_id_bytes = folder_id.encode("utf-8")
            rel_bytes = rel_target.as_posix().encode("utf-8")
            file_size = local_file.stat().st_size

            header = (
                MAGIC_HEADER
                + struct.pack("!BH", CMD_FILE_STREAM, len(f_id_bytes))
                + f_id_bytes
                + struct.pack("!H", len(rel_bytes))
                + rel_bytes
                + struct.pack("!Q", file_size)
            )

            try:
                self.sock.sendall(header)
                with open(local_file, "rb") as f:
                    while True:
                        chunk = f.read(CHUNK_STREAM_SIZE)
                        if not chunk:
                            break
                        self.sock.sendall(chunk)
                ack = recv_exact(self.sock, 1)
                return ack == b"\x00"
            except Exception:
                self._disconnect()
                return False

    def send_delete(self, folder_id: str, rel_target: PurePosixPath) -> bool:
        with self.lock:
            if not self.is_connected or not self.sock:
                return False

            f_id_bytes = folder_id.encode("utf-8")
            rel_bytes = rel_target.as_posix().encode("utf-8")
            header = (
                MAGIC_HEADER
                + struct.pack("!BH", CMD_DELETE, len(f_id_bytes))
                + f_id_bytes
                + struct.pack("!H", len(rel_bytes))
                + rel_bytes
            )
            try:
                self.sock.sendall(header)
                ack = recv_exact(self.sock, 1)
                return ack == b"\x00"
            except Exception:
                self._disconnect()
                return False

    def notify_sync_complete(self) -> bool:
        with self.lock:
            if not self.is_connected or not self.sock:
                return False
            try:
                self.sock.sendall(MAGIC_HEADER + struct.pack("!B", CMD_SYNC_END))
                ack = recv_exact(self.sock, 1)
                return ack == b"\x00"
            except Exception:
                self._disconnect()
                return False


class ThreadedHTTPServer(ThreadingMixIn, HTTPServer):
    daemon_threads = True


class AutoWiFiSyncEngine:
    def __init__(self, windows_folders: List[Dict[str, Any]], manual_ip: str = "", icon_manager: Optional[SafeTrayIconManager] = None):
        self.windows_folders = windows_folders
        self.manual_ip = manual_ip
        self.icon_manager = icon_manager

        self.task_queue: queue.Queue[Optional[Tuple[FileSystemEvent, Path, List[str], int, str]]] = queue.Queue()
        self.worker_thread = threading.Thread(target=self._process_queue, daemon=False)
        self.supervisor_thread = threading.Thread(target=self._maintain_connections, daemon=True)
        self._shutdown_event = threading.Event()

        self._clients: Dict[str, DeviceClient] = {}
        self._clients_lock = threading.Lock()

        ensure_firewall_rule()

        self._http_server = None
        self._start_http_manifest_server()
        self._start_udp_discovery_beacon()

    def _start_udp_discovery_beacon(self):
        """Broadcasts presence and listens for phones requesting immediate connection."""
        engine_ref = self

        def _udp_loop():
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                sock.bind(("0.0.0.0", UDP_BEACON_PORT))
            except Exception as e:
                print(f"[UDP Bind Error] {e}")
                return

            sock.settimeout(1.5)
            last_announce = 0

            while not engine_ref._shutdown_event.is_set():
                now = time.time()
                # Broadcast PC presence every 3 seconds
                if now - last_announce > 3.0:
                    last_announce = now
                    for local_ip in NetworkDiscovery.get_active_ipv4_subnets():
                        parts = local_ip.split(".")
                        if len(parts) == 4:
                            bcast_ip = f"{parts[0]}.{parts[1]}.{parts[2]}.255"
                            msg = f"MIRROR_PC_ANNOUNCE:{local_ip}".encode("utf-8")
                            try:
                                sock.sendto(msg, (bcast_ip, UDP_BEACON_PORT))
                            except Exception:
                                pass

                try:
                    data, addr = sock.recvfrom(1024)
                    text = data.decode("utf-8", errors="ignore").strip()

                    # Phone announcing itself: immediately connect TCP without waiting for subnet scan
                    if text.startswith("MIRROR_PHONE_ANNOUNCE:"):
                        phone_ip = text.split(":", 1)[1].strip() or addr[0]
                        threading.Thread(target=engine_ref._connect_single_ip, args=(phone_ip,), daemon=True).start()

                    elif text == "MIRROR_QUERY_PC":
                        # Direct query: reply with our IP
                        for local_ip in NetworkDiscovery.get_active_ipv4_subnets():
                            reply = f"MIRROR_PC_ANNOUNCE:{local_ip}".encode("utf-8")
                            try:
                                sock.sendto(reply, addr)
                            except Exception:
                                pass
                except socket.timeout:
                    continue
                except Exception:
                    pass

        threading.Thread(target=_udp_loop, daemon=True).start()

    def _start_http_manifest_server(self):
        engine_ref = self

        class ManifestRequestHandler(BaseHTTPRequestHandler):
            def log_message(self, format, *args):
                pass

            def do_GET(self):
                if self.path == "/config":
                    config_payload = engine_ref.get_folders_config_json()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.end_headers()
                    self.wfile.write(json.dumps(config_payload, indent=2).encode("utf-8"))

                elif self.path == "/manifests":
                    manifest_data = engine_ref.get_all_manifests_json()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.end_headers()
                    self.wfile.write(json.dumps(manifest_data, indent=2).encode("utf-8"))

                elif self.path.startswith("/trigger_sync"):
                    query_ip = None
                    if "?" in self.path:
                        query = self.path.split("?", 1)[1]
                        for param in query.split("&"):
                            if param.startswith("ip="):
                                query_ip = param.split("=", 1)[1]
                                break

                    if query_ip:
                        engine_ref.trigger_device_sync(query_ip)
                    else:
                        engine_ref.trigger_all_sync()

                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.end_headers()
                    self.wfile.write(b'{"status": "sync_triggered"}')
                else:
                    self.send_response(404)
                    self.end_headers()

        def _run_server():
            try:
                self._http_server = ThreadedHTTPServer(("0.0.0.0", HTTP_MANIFEST_PORT), ManifestRequestHandler)
                self._http_server.serve_forever()
            except Exception as e:
                sys.stderr.write(f"[HTTP Server Error] {e}\n")

        threading.Thread(target=_run_server, daemon=True).start()

    def get_folders_config_json(self) -> List[Dict[str, Any]]:
        payload = []
        for folder_info in self.windows_folders:
            p = Path(folder_info["path"]).resolve()
            payload.append({
                "id": get_folder_id(str(p)),
                "name": p.name,
                "local_path": str(p),
                "extensions": folder_info.get("extensions", ["*"]),
                "scrub_level": folder_info.get("scrub_level", 0)
            })
        return payload

    def get_all_manifests_json(self) -> Dict[str, Any]:
        folders_data = []
        for folder_info in self.windows_folders:
            local_root = Path(folder_info["path"]).resolve()
            allowed_exts = folder_info.get("extensions", ["*"])
            scrub_level = folder_info.get("scrub_level", 0)
            folder_id = get_folder_id(str(local_root))

            win_manifest: Dict[str, int] = {}
            if local_root.exists():
                for root, _, files in os.walk(local_root):
                    for f in files:
                        l_file = Path(root) / f
                        if not is_extension_allowed(l_file, allowed_exts):
                            continue
                        rel = l_file.relative_to(local_root)
                        target_posix = compute_target_rel_path(rel, scrub_level).as_posix()
                        win_manifest[target_posix] = l_file.stat().st_size

            folders_data.append({
                "id": folder_id,
                "name": local_root.name,
                "local_path": str(local_root),
                "scrub_level": scrub_level,
                "extensions": allowed_exts,
                "manifest": win_manifest
            })

        return {"folders": folders_data}

    def _connect_single_ip(self, ip: str):
        with self._clients_lock:
            if ip in self._clients and self._clients[ip].is_connected:
                return

        client = DeviceClient(ip)
        if client.connect():
            if client.send_config(self.windows_folders):
                with self._clients_lock:
                    self._clients[ip] = client
                print(f"[+] Connected to Android Device at {ip}:{TCP_DATA_PORT}")
                threading.Thread(target=self._initial_sync_device, args=(client,), daemon=True).start()
                self._update_tray_status()

    def trigger_device_sync(self, target_ip: str):
        with self._clients_lock:
            client = self._clients.get(target_ip)
        if client and client.is_connected:
            threading.Thread(target=self._initial_sync_device, args=(client,), daemon=True).start()
        else:
            self._connect_single_ip(target_ip)

    def trigger_all_sync(self):
        with self._clients_lock:
            clients = list(self._clients.values())
        for c in clients:
            if c.is_connected:
                threading.Thread(target=self._initial_sync_device, args=(c,), daemon=True).start()

    def _maintain_connections(self):
        while not self._shutdown_event.is_set():
            with self._clients_lock:
                dead_ips = [ip for ip, client in self._clients.items() if not client.is_connected]
                for ip in dead_ips:
                    self._clients[ip].close()
                    del self._clients[ip]
                connected_ips = set(self._clients.keys())

            active_ips = NetworkDiscovery.find_all_devices(self.manual_ip, exclude_ips=connected_ips)
            for ip in active_ips:
                if self._shutdown_event.is_set():
                    break
                self._connect_single_ip(ip)

            self._update_tray_status()
            time.sleep(3.0)

    def _update_tray_status(self):
        if not self.icon_manager:
            return
        with self._clients_lock:
            count = len(self._clients)
            if count == 0:
                self.icon_manager.set_status("Scanning Wi-Fi for devices...")
            elif count == 1:
                ip = list(self._clients.keys())[0]
                self.icon_manager.set_status(f"Connected (1 device: {ip})")
            else:
                self.icon_manager.set_status(f"Broadcasting to {count} devices")

    def _initial_sync_device(self, client: DeviceClient) -> None:
        for folder_info in self.windows_folders:
            local_root = Path(folder_info["path"]).resolve()
            allowed_exts = folder_info.get("extensions", ["*"])
            scrub_level = folder_info.get("scrub_level", 0)
            folder_id = get_folder_id(str(local_root))

            if not local_root.exists():
                local_root.mkdir(parents=True, exist_ok=True)

            win_manifest: Dict[str, int] = {}
            target_to_local_file: Dict[str, Path] = {}

            for root, _, files in os.walk(local_root):
                for f in files:
                    l_file = Path(root) / f
                    if not is_extension_allowed(l_file, allowed_exts):
                        continue
                    rel = l_file.relative_to(local_root)
                    target_posix = compute_target_rel_path(rel, scrub_level).as_posix()
                    file_size = l_file.stat().st_size
                    win_manifest[target_posix] = file_size
                    target_to_local_file[target_posix] = l_file

            report = client.exchange_manifest(folder_id, win_manifest)
            if not report:
                continue

            local_count = report.get("local_count", 0)
            remote_count = report.get("remote_count", len(win_manifest))
            deleted_count = report.get("deleted_count", 0)
            needed_files = report.get("needed", [])

            print(f"[Manifest Audit] {client.ip} [{local_root.name}]: "
                  f"Win={remote_count} | Android={local_count} | Pruned={deleted_count} | Transferring={len(needed_files)}")

            for target_posix in needed_files:
                if self._shutdown_event.is_set() or not client.is_connected:
                    break
                local_file = target_to_local_file.get(target_posix)
                if local_file and local_file.exists():
                    client.stream_file(folder_id, local_file, PurePosixPath(target_posix))

        client.notify_sync_complete()

    def _broadcast_stream_file(self, folder_id: str, local_file: Path, rel_target: PurePosixPath):
        if self.icon_manager:
            self.icon_manager.start_transfer(local_file.name)

        with self._clients_lock:
            clients = list(self._clients.values())

        def _send(c: DeviceClient):
            if c.is_connected:
                c.stream_file(folder_id, local_file, rel_target)

        with ThreadPoolExecutor(max_workers=max(1, len(clients))) as pool:
            pool.map(_send, clients)

        if self.icon_manager:
            self.icon_manager.stop_transfer()

    def _broadcast_delete(self, folder_id: str, rel_target: PurePosixPath):
        with self._clients_lock:
            clients = list(self._clients.values())

        def _del(c: DeviceClient):
            if c.is_connected:
                c.send_delete(folder_id, rel_target)

        with ThreadPoolExecutor(max_workers=max(1, len(clients))) as pool:
            pool.map(_del, clients)

    def _broadcast_sync_end(self):
        with self._clients_lock:
            clients = list(self._clients.values())

        def _end(c: DeviceClient):
            if c.is_connected:
                c.notify_sync_complete()

        with ThreadPoolExecutor(max_workers=max(1, len(clients))) as pool:
            pool.map(_end, clients)

    def enqueue_event(self, event: FileSystemEvent, local_root: Path, allowed_exts: List[str], scrub_level: int, folder_id: str) -> None:
        if not self._shutdown_event.is_set():
            self.task_queue.put((event, local_root, allowed_exts, scrub_level, folder_id))

    def start(self) -> None:
        self.supervisor_thread.start()
        self.worker_thread.start()

    def stop(self) -> None:
        self._shutdown_event.set()
        self.task_queue.put(None)
        if self.worker_thread.is_alive():
            self.worker_thread.join(timeout=3)

        if self._http_server:
            try:
                self._http_server.shutdown()
            except Exception:
                pass

        with self._clients_lock:
            for client in self._clients.values():
                client.close()
            self._clients.clear()

    def _process_queue(self) -> None:
        while True:
            item_data = self.task_queue.get(block=True)
            if item_data is None or self._shutdown_event.is_set():
                self.task_queue.task_done()
                break

            event, local_root, allowed_exts, scrub_level, folder_id = item_data
            try:
                self._handle_event(event, local_root, allowed_exts, scrub_level, folder_id)
            except Exception as e:
                sys.stderr.write(f"[Broadcast Exception] {e}\n")
            finally:
                self.task_queue.task_done()

            if self.task_queue.empty() and not self._shutdown_event.is_set():
                self._broadcast_sync_end()
                self._update_tray_status()

    def _handle_event(self, event: FileSystemEvent, local_root: Path, allowed_exts: List[str], scrub_level: int, folder_id: str) -> None:
        src_local = Path(event.src_path).resolve()
        rel_src = src_local.relative_to(local_root)
        target_rel = compute_target_rel_path(rel_src, scrub_level)

        if isinstance(event, (FileCreatedEvent, FileModifiedEvent)):
            if src_local.is_file() and is_extension_allowed(src_local, allowed_exts):
                self._broadcast_stream_file(folder_id, src_local, target_rel)

        elif isinstance(event, (FileDeletedEvent, DirDeletedEvent)):
            self._broadcast_delete(folder_id, target_rel)

        elif isinstance(event, (FileMovedEvent, DirMovedEvent)):
            dest_local = Path(event.dest_path).resolve()
            rel_dest = dest_local.relative_to(local_root)
            dest_target_rel = compute_target_rel_path(rel_dest, scrub_level)
            self._broadcast_delete(folder_id, target_rel)
            if dest_local.is_file() and is_extension_allowed(dest_local, allowed_exts):
                self._broadcast_stream_file(folder_id, dest_local, dest_target_rel)


class FolderSyncEventHandler(FileSystemEventHandler):
    def __init__(self, engine: AutoWiFiSyncEngine, local_root: Path, allowed_exts: List[str], scrub_level: int, folder_id: str):
        self.engine = engine
        self.local_root = local_root
        self.allowed_exts = allowed_exts
        self.scrub_level = scrub_level
        self.folder_id = folder_id

    def on_created(self, event: FileSystemEvent) -> None:
        self.engine.enqueue_event(event, self.local_root, self.allowed_exts, self.scrub_level, self.folder_id)

    def on_modified(self, event: FileSystemEvent) -> None:
        if not event.is_directory:
            self.engine.enqueue_event(event, self.local_root, self.allowed_exts, self.scrub_level, self.folder_id)

    def on_deleted(self, event: FileSystemEvent) -> None:
        self.engine.enqueue_event(event, self.local_root, self.allowed_exts, self.scrub_level, self.folder_id)

    def on_moved(self, event: FileSystemEvent) -> None:
        self.engine.enqueue_event(event, self.local_root, self.allowed_exts, self.scrub_level, self.folder_id)


def format_scrub_label(lvl: int) -> str:
    if lvl == 0:
        return "0 - Disabled (Full Tree)"
    return f"{lvl} - Max {lvl} {'Level' if lvl == 1 else 'Levels'} Deep"


def open_windows_folder_manager(on_save_callback):
    def _run():
        root = tk.Tk()
        root.title("Auto Wi-Fi Mirror Folders Configuration")
        root.geometry("840x580")
        root.minsize(700, 460)
        root.attributes("-topmost", True)

        cfg = ConfigManager.load()
        folders_list: List[Dict[str, Any]] = list(cfg.get("windows_folders", []))

        ip_frame = tk.Frame(root, padx=14, pady=8)
        ip_frame.pack(fill="x")
        tk.Label(ip_frame, text="Target IP(s) (comma-separated, or blank for full subnet scan):", font=("Segoe UI", 9, "bold")).pack(side="left")
        manual_ip_entry = tk.Entry(ip_frame, width=32)
        manual_ip_entry.insert(0, cfg.get("manual_ip", ""))
        manual_ip_entry.pack(side="left", padx=(8, 0))

        hdr = tk.Frame(root, padx=14, pady=4)
        hdr.pack(fill="x")
        tk.Label(hdr, text="Configured Windows Folders to Broadcast:", font=("Segoe UI", 10, "bold")).pack(anchor="w")

        tbl_frame = tk.Frame(root, padx=14)
        tbl_frame.pack(fill="both", expand=True)

        columns = ("path", "exts", "scrub")
        tree = ttk.Treeview(tbl_frame, columns=columns, show="headings", selectmode="browse")
        tree.heading("path", text="Windows Source Folder")
        tree.heading("exts", text="Matching Extensions")
        tree.heading("scrub", text="Scrub Level")
        tree.column("path", width=400)
        tree.column("exts", width=180)
        tree.column("scrub", width=180, anchor="center")

        scroll = ttk.Scrollbar(tbl_frame, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=scroll.set)
        tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

        def refresh_table():
            tree.delete(*tree.get_children())
            for idx, item_data in enumerate(folders_list):
                ext_str = ", ".join(item_data.get("extensions", ["*"]))
                scrub_str = format_scrub_label(item_data.get("scrub_level", 0))
                tree.insert("", "end", iid=str(idx), values=(item_data["path"], ext_str, scrub_str))

        refresh_table()

        def open_folder_editor(edit_idx: Optional[int] = None):
            dlg = tk.Toplevel(root)
            dlg.title("Edit Broadcast Folder" if edit_idx is not None else "Add Broadcast Folder")
            dlg.geometry("580x280")
            dlg.resizable(False, False)
            dlg.grab_set()

            init_path = folders_list[edit_idx]["path"] if edit_idx is not None else ""
            init_exts = ", ".join(folders_list[edit_idx].get("extensions", ["*"])) if edit_idx is not None else "*"
            init_scrub = folders_list[edit_idx].get("scrub_level", 0) if edit_idx is not None else 0

            tk.Label(dlg, text="Select Windows Source Directory:", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=14, pady=(14, 2))
            p_frame = tk.Frame(dlg, padx=14)
            p_frame.pack(fill="x")
            path_txt = tk.Entry(p_frame)
            path_txt.insert(0, init_path)
            path_txt.pack(side="left", fill="x", expand=True, padx=(0, 6))

            def pick_dir():
                chosen = filedialog.askdirectory(initialdir=path_txt.get() or Path.home())
                if chosen:
                    path_txt.delete(0, tk.END)
                    path_txt.insert(0, os.path.normpath(chosen))

            tk.Button(p_frame, text="Browse...", command=pick_dir).pack(side="right")

            tk.Label(dlg, text="File Filter Extensions (comma separated, e.g. .md, .png or * for all):", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=14, pady=(10, 2))
            ext_txt = tk.Entry(dlg)
            ext_txt.insert(0, init_exts)
            ext_txt.pack(fill="x", padx=14)

            tk.Label(dlg, text="Folder Scrubbing Level (flatten directory tree deeper than):", font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=14, pady=(10, 2))
            scrub_options = [
                "0 - Disabled (Full Tree)",
                "1 - Max 1 Level Deep",
                "2 - Max 2 Levels Deep",
                "3 - Max 3 Levels Deep",
                "4 - Max 4 Levels Deep",
                "5 - Max 5 Levels Deep"
            ]
            scrub_cb = ttk.Combobox(dlg, values=scrub_options, state="readonly")
            scrub_cb.current(min(init_scrub, 5))
            scrub_cb.pack(fill="x", padx=14)

            def commit():
                p = path_txt.get().strip()
                e_raw = ext_txt.get().strip()
                if not p:
                    messagebox.showerror("Validation", "Source directory path cannot be empty.", parent=dlg)
                    return

                item_payload = {
                    "path": p,
                    "extensions": normalize_extensions(e_raw),
                    "scrub_level": scrub_cb.current()
                }

                if edit_idx is not None:
                    folders_list[edit_idx] = item_payload
                else:
                    folders_list.append(item_payload)

                refresh_table()
                dlg.destroy()

            btn_box = tk.Frame(dlg, padx=14, pady=16)
            btn_box.pack(fill="x")
            tk.Button(btn_box, text="Cancel", width=10, command=dlg.destroy).pack(side="right", padx=(6, 0))
            tk.Button(btn_box, text="Apply", bg="#0284C7", fg="white", width=12, command=commit).pack(side="right")

        btn_box = tk.Frame(root, padx=14, pady=8)
        btn_box.pack(fill="x")

        def add_item():
            open_folder_editor(None)

        def edit_item():
            sel = tree.selection()
            if sel:
                open_folder_editor(int(sel[0]))

        def remove_item():
            sel = tree.selection()
            if sel:
                del folders_list[int(sel[0])]
                refresh_table()

        tk.Button(btn_box, text="+ Add Windows Folder...", command=add_item, bg="#0284C7", fg="white").pack(side="left", padx=(0, 6))
        tk.Button(btn_box, text="Edit Selected", command=edit_item).pack(side="left", padx=(0, 6))
        tk.Button(btn_box, text="Remove Selected", command=remove_item).pack(side="left")

        foot = tk.Frame(root, padx=14, pady=12)
        foot.pack(fill="x")

        def save_and_apply():
            if not folders_list:
                messagebox.showerror("Error", "Please configure at least one folder.", parent=root)
                return
            new_cfg = {
                "manual_ip": manual_ip_entry.get().strip(),
                "windows_folders": folders_list
            }
            ConfigManager.save(new_cfg)
            on_save_callback(new_cfg)
            root.destroy()

        tk.Button(foot, text="Cancel", width=10, command=root.destroy).pack(side="right", padx=(6, 0))
        tk.Button(foot, text="Save & Broadcast", bg="#16A34A", fg="white", width=18, command=save_and_apply).pack(side="right")

        root.mainloop()

    threading.Thread(target=_run, daemon=True).start()


class SystemTrayApp:
    def __init__(self):
        self.config = ConfigManager.load()
        self.engine: Optional[AutoWiFiSyncEngine] = None
        self.observer: Optional[Observer] = None
        self.icon: Optional[pystray.Icon] = None
        self.icon_manager = SafeTrayIconManager()

    def restart_sync_engine(self, new_config: Optional[Dict[str, Any]] = None):
        if new_config:
            self.config = new_config

        if self.observer:
            self.observer.stop()
            self.observer.join(timeout=3)
            self.observer = None

        if self.engine:
            self.engine.stop()
            self.engine = None

        folders = self.config.get("windows_folders", [])
        if not folders:
            return

        self.engine = AutoWiFiSyncEngine(
            windows_folders=folders,
            manual_ip=self.config.get("manual_ip", ""),
            icon_manager=self.icon_manager
        )
        self.engine.start()

        self.observer = Observer()
        for folder_info in folders:
            loc = Path(folder_info["path"]).resolve()
            loc.mkdir(parents=True, exist_ok=True)
            fid = get_folder_id(str(loc))
            handler = FolderSyncEventHandler(
                self.engine,
                local_root=loc,
                allowed_exts=folder_info.get("extensions", ["*"]),
                scrub_level=folder_info.get("scrub_level", 0),
                folder_id=fid
            )
            self.observer.schedule(handler, path=str(loc), recursive=True)

        self.observer.start()

    def quit_app(self, icon, item):
        if self.observer:
            self.observer.stop()
            self.observer.join(timeout=3)
        if self.engine:
            self.engine.stop()
        if self.icon:
            self.icon.stop()

    def run(self):
        self.restart_sync_engine()

        menu = Menu(
            item("Wi-Fi Auto Stream Sync (Broadcast)", None, enabled=False),
            Menu.SEPARATOR,
            item("⚡ Verify Manifests & Sync Now", lambda icon, item: self.engine.trigger_all_sync() if self.engine else None),
            item("Configure Windows Folders...", lambda icon, item: open_windows_folder_manager(self.restart_sync_engine)),
            item("Start with Windows", lambda icon, item: WindowsStartup.toggle(not WindowsStartup.is_enabled()), checked=lambda item: WindowsStartup.is_enabled()),
            Menu.SEPARATOR,
            item("Quit", self.quit_app),
        )

        self.icon = pystray.Icon(
            APP_NAME,
            icon=create_dynamic_icon(syncing=False),
            title="Wi-Fi Sync | Scanning...",
            menu=menu,
        )
        self.icon_manager.set_icon(self.icon)
        self.icon.run()


if __name__ == "__main__":
    guard = SingleInstanceGuard()
    if guard.is_running():
        ctypes.windll.user32.MessageBoxW(0, "Wi-Fi Stream Sync is already running in tray.", "Wi-Fi Sync", 0x40 | 0x0)
        sys.exit(0)

    try:
        app = SystemTrayApp()
        app.run()
    finally:
        guard.release()