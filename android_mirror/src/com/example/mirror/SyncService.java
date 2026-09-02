package com.example.mirror;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.media.MediaScannerConnection;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Environment;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.MediaStore;
import java.io.*;
import java.net.*;
import java.util.*;
import org.json.JSONArray;
import org.json.JSONObject;

public class SyncService extends Service {
    public static final String ACTION_STATUS_UPDATE = "com.example.mirror.STATUS_UPDATE";
    public static final String ACTION_CONFIG_REFRESH = "com.example.mirror.CONFIG_REFRESH";
    public static final String ACTION_MANIFEST_VERIFIED = "com.example.mirror.MANIFEST_VERIFIED";
    public static final String ACTION_PC_DISCOVERED = "com.example.mirror.PC_DISCOVERED";
    public static final String ACTION_FETCH_CONFIG = "com.example.mirror.FETCH_CONFIG";

    public static final int TCP_DATA_PORT = 58421;
    public static final int HTTP_MANIFEST_PORT = 58422;
    public static final int UDP_BEACON_PORT = 58423;

    public static final byte CMD_PING = 0x00;
    public static final byte CMD_CONFIG = 0x01;
    public static final byte CMD_MANIFEST_EXCHANGE = 0x02;
    public static final byte CMD_FILE_STREAM = 0x03;
    public static final byte CMD_DELETE = 0x04;
    public static final byte CMD_SYNC_END = 0x05;
    public static final byte CMD_GET_DEVICE_INFO = 0x06;
    public static final byte CMD_LIST_DIR = 0x07;
    public static final byte CMD_PULL_FILE = 0x08;
    public static final byte CMD_PUSH_FILE_DIRECT = 0x09;
    public static final byte CMD_DELETE_PATH_DIRECT = 0x0A;
    public static final byte CMD_MKDIR_DIRECT = 0x0B;
    public static final byte CMD_WAKE_SYNC = 0x0C;

    private static final String CHANNEL_ID = "mirror_tcp_channel";
    private static final int NOTIF_ID = 505;
    private static final String PREFS_NAME = "FolderMappingsPrefs";

    private PowerManager.WakeLock wakeLock;
    private WifiManager.MulticastLock multicastLock;
    private NotificationManager notificationManager;
    private ServerSocket serverSocket;

    private Thread tcpServerThread;
    private Thread udpBeaconThread;
    private volatile boolean isRunning = false;

    public native int syncDirectoryNative(String src, String dst, boolean mirrorExact, int scrubLevel);

    static {
        try {
            System.loadLibrary("native-sync");
        } catch (UnsatisfiedLinkError ignored) {}
    }

    public interface ConfigUpdateListener {
        void onConfigUpdated(String jsonConfig);
    }
    public static ConfigUpdateListener activeListener = null;

    @Override
    public void onCreate() {
        super.onCreate();
        createNotificationChannel();
        notificationManager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);

        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (pm != null) {
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "MirrorSync::TransferLock");
            wakeLock.setReferenceCounted(false);
        }

        WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wm != null) {
            try {
                multicastLock = wm.createMulticastLock("MirrorSync::DiscoveryLock");
                multicastLock.setReferenceCounted(false);
            } catch (Exception ignored) {}
        }
    }

    private synchronized void acquireTransferWakeLock(long timeoutMs) {
        try {
            if (wakeLock != null) wakeLock.acquire(timeoutMs);
        } catch (Exception ignored) {}
    }

    private synchronized void releaseTransferWakeLock() {
        try {
            if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        } catch (Exception ignored) {}
    }

    private synchronized void acquireTemporaryMulticastLock(long timeoutMs) {
        try {
            if (multicastLock != null && !multicastLock.isHeld()) {
                multicastLock.acquire();
                new Handler(Looper.getMainLooper()).postDelayed(() -> {
                    try {
                        if (multicastLock != null && multicastLock.isHeld()) multicastLock.release();
                    } catch (Exception ignored) {}
                }, timeoutMs);
            }
        } catch (Exception ignored) {}
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForeground(NOTIF_ID, buildNotification("Listening on port " + TCP_DATA_PORT));
        if (!isRunning) {
            isRunning = true;
            startNetworkServer();
            startUdpBeaconListener();
        }

        if (intent != null && ACTION_FETCH_CONFIG.equals(intent.getAction())) {
            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            String pcIp = prefs.getString("last_pc_ip", "");
            if (!pcIp.isEmpty()) fetchConfigFromPc(this, pcIp, null);
        }

        return START_STICKY;
    }

    private void startUdpBeaconListener() {
        udpBeaconThread = new Thread(() -> {
            DatagramSocket socket = null;
            try {
                socket = new DatagramSocket(null);
                socket.setReuseAddress(true);
                socket.setBroadcast(true);
                socket.bind(new InetSocketAddress("0.0.0.0", UDP_BEACON_PORT));

                byte[] buf = new byte[1024];
                DatagramPacket packet = new DatagramPacket(buf, buf.length);

                while (isRunning) {
                    try {
                        socket.receive(packet);
                        String msg = new String(packet.getData(), 0, packet.getLength(), "UTF-8").trim();

                        if (msg.startsWith("MIRROR_PC_ANNOUNCE:")) {
                            String pcIp = msg.split(":", 2)[1].trim();
                            if (pcIp.isEmpty()) pcIp = packet.getAddress().getHostAddress();

                            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                            String currentSaved = prefs.getString("last_pc_ip", "");
                            if (!pcIp.equals(currentSaved)) {
                                prefs.edit().putString("last_pc_ip", pcIp).apply();
                            }

                            Intent intent = new Intent(ACTION_PC_DISCOVERED);
                            intent.putExtra("pc_ip", pcIp);
                            intent.setPackage(getPackageName());
                            sendBroadcast(intent);

                            ensureConfigLoadedFromPc(pcIp);
                        }
                    } catch (Exception ignored) {}
                }
            } catch (Exception ignored) {
            } finally {
                if (socket != null && !socket.isClosed()) socket.close();
            }
        });
        udpBeaconThread.start();
    }

    public static void broadcastPresenceToPc(final Context context) {
        new Thread(() -> {
            DatagramSocket socket = null;
            try {
                if (context instanceof SyncService) {
                    ((SyncService) context).acquireTemporaryMulticastLock(15000);
                }
                socket = new DatagramSocket();
                socket.setBroadcast(true);
                byte[] data = "MIRROR_QUERY_PC".getBytes("UTF-8");
                DatagramPacket packet = new DatagramPacket(
                        data, data.length, InetAddress.getByName("255.255.255.255"), UDP_BEACON_PORT
                );
                socket.send(packet);
            } catch (Exception ignored) {
            } finally {
                if (socket != null) socket.close();
            }
        }).start();
    }

    private void startNetworkServer() {
        tcpServerThread = new Thread(() -> {
            try {
                serverSocket = new ServerSocket();
                serverSocket.setReuseAddress(true);
                serverSocket.bind(new InetSocketAddress("0.0.0.0", TCP_DATA_PORT), 50);

                broadcastStatus("Server Online | Listening on :" + TCP_DATA_PORT);
                updateNotification("Ready for PC (Port " + TCP_DATA_PORT + ")");

                while (isRunning && !serverSocket.isClosed()) {
                    try {
                        final Socket client = serverSocket.accept();
                        client.setTcpNoDelay(true);
                        client.setKeepAlive(true);
                        new Thread(() -> handleClient(client)).start();
                    } catch (IOException e) {
                        if (!isRunning) break;
                    }
                }
            } catch (IOException e) {
                broadcastStatus("Server Port Error: " + e.getMessage());
            }
        });
        tcpServerThread.start();
    }

    private void handleClient(Socket socket) {
        try (DataInputStream dis = new DataInputStream(new BufferedInputStream(socket.getInputStream(), 131072));
             DataOutputStream dos = new DataOutputStream(new BufferedOutputStream(socket.getOutputStream(), 131072))) {

            while (isRunning && !socket.isClosed()) {
                byte m1, m2;
                try {
                    m1 = dis.readByte();
                    m2 = dis.readByte();
                } catch (EOFException e) {
                    break;
                }

                if (m1 != (byte) 0xAA || m2 != (byte) 0x55) break;

                acquireTransferWakeLock(180000L);
                int cmd = dis.readByte();

                if (cmd == CMD_WAKE_SYNC) {
                    // Demand condition 1: File changed on PC, immediate synchronization woken
                    broadcastStatus("Waking Up: Remote Changes Detected");
                    dos.writeByte(0x00);
                    dos.flush();

                    String pcIp = socket.getInetAddress().getHostAddress();
                    ensureConfigLoadedFromPc(pcIp);
                    triggerManifestSyncFromAndroid(pcIp);

                } else if (cmd == CMD_PING) {
                    String pcIp = socket.getInetAddress().getHostAddress();
                    SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                    prefs.edit().putString("last_pc_ip", pcIp).apply();

                    Intent intent = new Intent(ACTION_PC_DISCOVERED);
                    intent.putExtra("pc_ip", pcIp);
                    intent.setPackage(getPackageName());
                    sendBroadcast(intent);

                    dos.writeByte(0x00);
                    dos.flush();
                    ensureConfigLoadedFromPc(pcIp);

                } else if (cmd == CMD_CONFIG) {
                    String pcIp = socket.getInetAddress().getHostAddress();
                    broadcastStatus("Connected to PC (" + pcIp + ")");

                    SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                    prefs.edit().putString("last_pc_ip", pcIp).apply();

                    int len = dis.readInt();
                    byte[] data = new byte[len];
                    dis.readFully(data);
                    String jsonConfig = new String(data, "UTF-8");

                    File configFile = new File(getFilesDir(), "windows_sources.json");
                    try (FileOutputStream fos = new FileOutputStream(configFile)) {
                        fos.write(data);
                    }

                    if (activeListener != null) {
                        activeListener.onConfigUpdated(jsonConfig);
                    }
                    sendBroadcast(new Intent(ACTION_CONFIG_REFRESH).setPackage(getPackageName()));

                    dos.writeByte(0x00);
                    dos.flush();
                    broadcastStatus("Configuration Synchronized");

                } else if (cmd == CMD_MANIFEST_EXCHANGE) {
                    int fIdLen = dis.readUnsignedShort();
                    byte[] fIdBytes = new byte[fIdLen];
                    dis.readFully(fIdBytes);
                    String folderId = new String(fIdBytes, "UTF-8");

                    int payloadLen = dis.readInt();
                    byte[] payloadBytes = new byte[payloadLen];
                    dis.readFully(payloadBytes);
                    
                    JSONObject payloadObj = new JSONObject(new String(payloadBytes, "UTF-8"));
                    JSONObject winManifestObj = payloadObj.has("files") ? payloadObj.getJSONObject("files") : payloadObj;

                    JSONObject report = evaluateManifest(folderId, winManifestObj);

                    byte[] respBytes = report.toString().getBytes("UTF-8");
                    dos.writeInt(respBytes.length);
                    dos.write(respBytes);
                    dos.flush();

                } else if (cmd == CMD_FILE_STREAM) {
                    int fIdLen = dis.readUnsignedShort();
                    byte[] fIdBytes = new byte[fIdLen];
                    dis.readFully(fIdBytes);
                    String folderId = new String(fIdBytes, "UTF-8");

                    int relLen = dis.readUnsignedShort();
                    byte[] relBytes = new byte[relLen];
                    dis.readFully(relBytes);
                    String rawRel = new String(relBytes, "UTF-8");
                    String relPath = sanitizeRemotePath(rawRel, getFolderNameById(folderId));

                    long fileSize = dis.readLong();

                    SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                    boolean isEnabled = prefs.getBoolean(folderId + "_sync_enabled", true);
                    String targetRoot = prefs.getString(folderId, null);
                    if (targetRoot == null || targetRoot.isEmpty()) {
                        targetRoot = getTargetDirectoryFallback(folderId);
                    }

                    if (!isEnabled) {
                        skipStreamBytes(dis, fileSize);
                        dos.writeByte(0x00);
                        dos.flush();
                        continue;
                    }

                    File targetFile = new File(targetRoot, relPath);
                    File parent = targetFile.getParentFile();
                    if (parent != null && !parent.exists()) parent.mkdirs();

                    File tempFile = new File(targetFile.getAbsolutePath() + ".tmp");

                    broadcastStatus("Receiving: " + targetFile.getName());
                    updateNotification("Receiving: " + targetFile.getName());

                    try (FileOutputStream fos = new FileOutputStream(tempFile)) {
                        byte[] buffer = new byte[65536];
                        long remaining = fileSize;
                        while (remaining > 0) {
                            int read = dis.read(buffer, 0, (int) Math.min(buffer.length, remaining));
                            if (read == -1) break;
                            fos.write(buffer, 0, read);
                            remaining -= read;
                        }
                    }

                    if (targetFile.exists()) targetFile.delete();
                    tempFile.renameTo(targetFile);

                    scanFileWithMediaScanner(targetFile);

                    dos.writeByte(0x00);
                    dos.flush();

                } else if (cmd == CMD_DELETE) {
                    int fIdLen = dis.readUnsignedShort();
                    byte[] fIdBytes = new byte[fIdLen];
                    dis.readFully(fIdBytes);
                    String folderId = new String(fIdBytes, "UTF-8");

                    int relLen = dis.readUnsignedShort();
                    byte[] relBytes = new byte[relLen];
                    dis.readFully(relBytes);
                    String rawRel = new String(relBytes, "UTF-8");
                    String relPath = sanitizeRemotePath(rawRel, getFolderNameById(folderId));

                    SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                    boolean isEnabled = prefs.getBoolean(folderId + "_sync_enabled", true);
                    String targetRoot = prefs.getString(folderId, null);
                    if (targetRoot == null || targetRoot.isEmpty()) {
                        targetRoot = getTargetDirectoryFallback(folderId);
                    }

                    if (isEnabled && !relPath.isEmpty() && !relPath.equals(".")) {
                        File target = new File(targetRoot, relPath);
                        if (target.exists() && !target.equals(new File(targetRoot))) {
                            deleteRecursive(target);
                            rescanMediaStorePath(target.getAbsolutePath());
                        }
                    }
                    dos.writeByte(0x00);
                    dos.flush();

                } else if (cmd == CMD_SYNC_END) {
                    broadcastStatus("Sync Completed (Verified)");
                    updateNotification("Sync Completed (Verified)");
                    dos.writeByte(0x00);
                    dos.flush();
                    releaseTransferWakeLock();

                } else if (cmd == CMD_GET_DEVICE_INFO) {
                    JSONObject dev = new JSONObject();
                    dev.put("model", Build.MODEL != null ? Build.MODEL : "Android Device");
                    dev.put("manufacturer", Build.MANUFACTURER != null ? Build.MANUFACTURER : "Unknown");
                    dev.put("version", Build.VERSION.RELEASE != null ? Build.VERSION.RELEASE : "");
                    dev.put("sdk", Build.VERSION.SDK_INT);

                    JSONArray roots = new JSONArray();
                    File ext = Environment.getExternalStorageDirectory();
                    if (ext != null) {
                        JSONObject r = new JSONObject();
                        r.put("name", "Internal Storage");
                        r.put("path", ext.getAbsolutePath());
                        roots.put(r);
                    }

                    SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
                    File configFile = new File(getFilesDir(), "windows_sources.json");
                    if (configFile.exists()) {
                        try (FileInputStream fis = new FileInputStream(configFile)) {
                            byte[] b = new byte[(int) configFile.length()];
                            fis.read(b);
                            JSONArray arr = new JSONArray(new String(b, "UTF-8"));
                            for (int i = 0; i < arr.length(); i++) {
                                JSONObject o = arr.getJSONObject(i);
                                String fid = o.optString("id");
                                String name = o.optString("name");
                                String target = prefs.getString(fid, "/storage/emulated/0/" + name);
                                JSONObject r = new JSONObject();
                                r.put("name", "Synced: " + name);
                                r.put("path", target);
                                roots.put(r);
                            }
                        } catch (Exception ignored) {}
                    }
                    dev.put("root_dirs", roots);

                    byte[] payload = dev.toString().getBytes("UTF-8");
                    dos.writeByte(0x00);
                    dos.writeInt(payload.length);
                    dos.write(payload);
                    dos.flush();

                } else if (cmd == CMD_LIST_DIR) {
                    int pathLen = dis.readUnsignedShort();
                    byte[] pathBytes = new byte[pathLen];
                    dis.readFully(pathBytes);
                    String targetPath = new String(pathBytes, "UTF-8");

                    File dir = new File(targetPath);
                    JSONObject res = new JSONObject();
                    res.put("path", targetPath);
                    res.put("exists", dir.exists());

                    JSONArray items = new JSONArray();
                    if (dir.exists() && dir.isDirectory()) {
                        File[] list = dir.listFiles();
                        if (list != null) {
                            Arrays.sort(list, (f1, f2) -> {
                                if (f1.isDirectory() && !f2.isDirectory()) return -1;
                                if (!f1.isDirectory() && f2.isDirectory()) return 1;
                                return f1.getName().compareToIgnoreCase(f2.getName());
                            });

                            for (File f : list) {
                                JSONObject item = new JSONObject();
                                item.put("name", f.getName());
                                item.put("path", f.getAbsolutePath());
                                item.put("is_dir", f.isDirectory());
                                item.put("size", f.isDirectory() ? 0 : f.length());
                                item.put("last_modified", f.lastModified());
                                items.put(item);
                            }
                        }
                    }
                    res.put("items", items);

                    byte[] payload = res.toString().getBytes("UTF-8");
                    dos.writeByte(0x00);
                    dos.writeInt(payload.length);
                    dos.write(payload);
                    dos.flush();

                } else if (cmd == CMD_PULL_FILE) {
                    int pathLen = dis.readUnsignedShort();
                    byte[] pathBytes = new byte[pathLen];
                    dis.readFully(pathBytes);
                    String targetPath = new String(pathBytes, "UTF-8");

                    File file = new File(targetPath);
                    if (!file.exists() || !file.isFile() || !file.canRead()) {
                        dos.writeByte(0x01);
                        dos.flush();
                    } else {
                        dos.writeByte(0x00);
                        long len = file.length();
                        dos.writeLong(len);
                        dos.flush();

                        byte[] buf = new byte[65536];
                        try (FileInputStream fis = new FileInputStream(file)) {
                            long remaining = len;
                            while (remaining > 0) {
                                int r = fis.read(buf, 0, (int) Math.min(buf.length, remaining));
                                if (r == -1) break;
                                dos.write(buf, 0, r);
                                remaining -= r;
                            }
                        }
                        dos.flush();
                    }

                } else if (cmd == CMD_PUSH_FILE_DIRECT) {
                    int pathLen = dis.readUnsignedShort();
                    byte[] pathBytes = new byte[pathLen];
                    dis.readFully(pathBytes);
                    String targetPath = new String(pathBytes, "UTF-8");
                    long fileSize = dis.readLong();

                    File targetFile = new File(targetPath);
                    File parent = targetFile.getParentFile();
                    if (parent != null && !parent.exists()) parent.mkdirs();

                    File tempFile = new File(targetPath + ".upload_tmp");
                    try (FileOutputStream fos = new FileOutputStream(tempFile)) {
                        byte[] buffer = new byte[65536];
                        long remaining = fileSize;
                        while (remaining > 0) {
                            int r = dis.read(buffer, 0, (int) Math.min(buffer.length, remaining));
                            if (r == -1) break;
                            fos.write(buffer, 0, r);
                            remaining -= r;
                        }
                    }
                    if (targetFile.exists()) targetFile.delete();
                    tempFile.renameTo(targetFile);

                    scanFileWithMediaScanner(targetFile);

                    dos.writeByte(0x00);
                    dos.flush();

                } else if (cmd == CMD_DELETE_PATH_DIRECT) {
                    int pathLen = dis.readUnsignedShort();
                    byte[] pathBytes = new byte[pathLen];
                    dis.readFully(pathBytes);
                    String targetPath = new String(pathBytes, "UTF-8");

                    File target = new File(targetPath);
                    if (target.exists()) {
                        deleteRecursive(target);
                        rescanMediaStorePath(targetPath);
                    }
                    dos.writeByte(0x00);
                    dos.flush();

                } else if (cmd == CMD_MKDIR_DIRECT) {
                    int pathLen = dis.readUnsignedShort();
                    byte[] pathBytes = new byte[pathLen];
                    dis.readFully(pathBytes);
                    String targetPath = new String(pathBytes, "UTF-8");

                    File target = new File(targetPath);
                    if (!target.exists()) {
                        target.mkdirs();
                    }
                    dos.writeByte(0x00);
                    dos.flush();
                }
            }
        } catch (Exception ignored) {
        } finally {
            releaseTransferWakeLock();
            try {
                socket.close();
            } catch (IOException ignored) {}
            broadcastStatus("Server Online | Listening on :" + TCP_DATA_PORT);
        }
    }

    public static void fetchConfigFromPc(final Context context, final String pcIp, final Runnable onComplete) {
        new Thread(() -> {
            try {
                URL url = new URL("http://" + pcIp + ":" + HTTP_MANIFEST_PORT + "/config");
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setConnectTimeout(3000);
                conn.setReadTimeout(4000);
                conn.setRequestMethod("GET");

                if (conn.getResponseCode() == 200) {
                    BufferedReader in = new BufferedReader(new InputStreamReader(conn.getInputStream(), "UTF-8"));
                    StringBuilder sb = new StringBuilder();
                    String line;
                    while ((line = in.readLine()) != null) sb.append(line);
                    in.close();

                    String jsonConfig = sb.toString();
                    File configFile = new File(context.getFilesDir(), "windows_sources.json");
                    try (FileOutputStream fos = new FileOutputStream(configFile)) {
                        fos.write(jsonConfig.getBytes("UTF-8"));
                    }

                    if (activeListener != null) {
                        activeListener.onConfigUpdated(jsonConfig);
                    }
                    context.sendBroadcast(new Intent(ACTION_CONFIG_REFRESH).setPackage(context.getPackageName()));
                }
            } catch (Exception ignored) {
            } finally {
                if (onComplete != null) onComplete.run();
            }
        }).start();
    }

    private void ensureConfigLoadedFromPc(String pcIp) {
        File configFile = new File(getFilesDir(), "windows_sources.json");
        if (!configFile.exists() || configFile.length() == 0) {
            fetchConfigFromPc(this, pcIp, null);
        }
    }

    private void triggerManifestSyncFromAndroid(final String pcIp) {
        new Thread(() -> {
            try {
                URL url = new URL("http://" + pcIp + ":" + HTTP_MANIFEST_PORT + "/trigger_sync?ip=" + URLEncoder.encode(getDeviceIpAddress(), "UTF-8"));
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setConnectTimeout(3000);
                conn.getResponseCode();
                conn.disconnect();
            } catch (Exception ignored) {}
        }).start();
    }

    private String getDeviceIpAddress() {
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (nif.isLoopback() || !nif.isUp()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (!addr.isLoopbackAddress() && addr instanceof Inet4Address) {
                        return addr.getHostAddress();
                    }
                }
            }
        } catch (Exception ignored) {}
        return "";
    }

    private void skipStreamBytes(DataInputStream dis, long totalBytes) throws IOException {
        byte[] buffer = new byte[65536];
        long remaining = totalBytes;
        while (remaining > 0) {
            int read = dis.read(buffer, 0, (int) Math.min(buffer.length, remaining));
            if (read == -1) break;
            remaining -= read;
        }
    }

    private String getTargetDirectoryFallback(String folderId) {
        File configFile = new File(getFilesDir(), "windows_sources.json");
        if (!configFile.exists()) return "/storage/emulated/0/SyncWorkspace";
        try (FileInputStream fis = new FileInputStream(configFile)) {
            byte[] data = new byte[(int) configFile.length()];
            fis.read(data);
            JSONArray arr = new JSONArray(new String(data, "UTF-8"));
            for (int i = 0; i < arr.length(); i++) {
                JSONObject obj = arr.getJSONObject(i);
                if (folderId.equals(obj.optString("id"))) {
                    return "/storage/emulated/0/" + obj.optString("name", "SyncWorkspace");
                }
            }
        } catch (Exception ignored) {}
        return "/storage/emulated/0/SyncWorkspace";
    }

    private String getFolderNameById(String folderId) {
        File configFile = new File(getFilesDir(), "windows_sources.json");
        if (!configFile.exists()) return "";
        try (FileInputStream fis = new FileInputStream(configFile)) {
            byte[] data = new byte[(int) configFile.length()];
            fis.read(data);
            JSONArray arr = new JSONArray(new String(data, "UTF-8"));
            for (int i = 0; i < arr.length(); i++) {
                JSONObject obj = arr.getJSONObject(i);
                if (folderId.equals(obj.optString("id"))) {
                    return obj.optString("name", "");
                }
            }
        } catch (Exception ignored) {}
        return "";
    }

    public static String normalizePath(String path) {
        if (path == null) return "";
        // Unify backslashes, convert repeated slashes, and strip leading/trailing slashes
        String p = path.replace('\\', '/').trim();
        if (p.length() >= 2 && p.charAt(1) == ':') {
            p = p.substring(2);
        }
        p = p.replaceAll("/+", "/");
        while (p.startsWith("./")) p = p.substring(2);
        while (p.startsWith("/")) p = p.substring(1);
        while (p.endsWith("/")) p = p.substring(0, p.length() - 1);
        return p;
    }

    public static String sanitizeRemotePath(String rawKey, String folderName) {
        // Do NOT strip subfolder names matching folderName — keys from PC are already relative
        return normalizePath(rawKey);
    }

    public static String getRelativePath(File root, File file) {
        try {
            URI baseUri = root.toURI();
            URI fileUri = file.toURI();
            String rel = baseUri.relativize(fileUri).getPath();
            return normalizePath(rel);
        } catch (Exception e) {
            String rootAbs = normalizePath(root.getAbsolutePath());
            String fileAbs = normalizePath(file.getAbsolutePath());
            if (fileAbs.startsWith(rootAbs)) {
                return normalizePath(fileAbs.substring(rootAbs.length()));
            }
            return file.getName();
        }
    }

    private JSONObject evaluateManifest(String folderId, JSONObject winManifest) {
        JSONObject report = new JSONObject();
        JSONArray neededArr = new JSONArray();

        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        boolean isEnabled = prefs.getBoolean(folderId + "_sync_enabled", true);
        boolean mirrorExact = prefs.getBoolean(folderId + "_mirror_exact", false);
        String folderName = getFolderNameById(folderId);

        String rawPrettyManifest;
        try {
            rawPrettyManifest = winManifest.toString(2);
        } catch (Exception e) {
            rawPrettyManifest = winManifest.toString();
        }

        String targetRoot = prefs.getString(folderId, null);
        if (targetRoot == null || targetRoot.trim().isEmpty()) {
            targetRoot = getTargetDirectoryFallback(folderId);
            prefs.edit().putString(folderId, targetRoot).apply();
        }

        try {
            if (!isEnabled) {
                report.put("needed", neededArr);
                report.put("local_count", 0);
                report.put("remote_count", winManifest.length());
                report.put("deleted_count", 0);
                report.put("status_note", "Sync Ignored (Disabled)");

                Intent auditIntent = new Intent(ACTION_MANIFEST_VERIFIED);
                auditIntent.putExtra("folder_id", folderId);
                auditIntent.putExtra("raw_manifest", rawPrettyManifest);
                auditIntent.putExtra("status_note", "Sync Ignored (Disabled)");
                auditIntent.setPackage(getPackageName());
                sendBroadcast(auditIntent);
                return report;
            }

            File targetDir = new File(targetRoot);
            if (!targetDir.exists()) {
                targetDir.mkdirs();
            }

            // Keep consistent reference base to prevent symlink drift (/storage vs /data/media)
            File baseTargetDir = targetDir.getAbsoluteFile();

            Map<String, Long> localManifest = new HashMap<>();
            scanDirectoryRecursively(baseTargetDir, baseTargetDir, localManifest);

            Map<String, String> winCaseMap = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
            Map<String, Long> winSizeMap = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
            
            Iterator<String> winKeys = winManifest.keys();
            while (winKeys.hasNext()) {
                String rawKey = winKeys.next();
                String normKey = normalizePath(rawKey);
                if (!normKey.isEmpty()) {
                    winCaseMap.put(normKey, rawKey);
                    winSizeMap.put(normKey, winManifest.getLong(rawKey));
                }
            }

            // android.util.Log.e("MIRROR_DEBUG", "=================== EVALUATE MANIFEST AUDIT ===================");
            // android.util.Log.e("MIRROR_DEBUG", "Folder ID: " + folderId + " | TargetRoot: " + targetRoot);
            // android.util.Log.e("MIRROR_DEBUG", "mirrorExact setting: " + mirrorExact);
            // android.util.Log.e("MIRROR_DEBUG", "--- [WINDOWS MANIFEST KEYS RECEIVED (" + winCaseMap.size() + ")] ---");
            // for (String wk : winCaseMap.keySet()) {
            //     android.util.Log.e("MIRROR_DEBUG", "   WIN KEY: [" + wk + "] (raw: [" + winCaseMap.get(wk) + "])");
            // }
            // android.util.Log.e("MIRROR_DEBUG", "--- [ANDROID LOCAL FILES FOUND (" + localManifest.size() + ")] ---");
            // for (String lk : localManifest.keySet()) {
            //     android.util.Log.e("MIRROR_DEBUG", "   LOC KEY: [" + lk + "]");
            // }

            int deletedCount = 0;

            if (mirrorExact && !winCaseMap.isEmpty() && winManifest.length() > 0) {
                List<String> localKeys = new ArrayList<>(localManifest.keySet());
                for (String localRelPath : localKeys) {
                    String normLocal = normalizePath(localRelPath);
                    if (normLocal.isEmpty() || normLocal.equals(".")) {
                        //android.util.Log.e("MIRROR_DEBUG", "SKIP EMPTY OR ROOT: [" + localRelPath + "]");
                        continue;
                    }

                    boolean foundInWindows = winCaseMap.containsKey(normLocal);
                    //android.util.Log.e("MIRROR_DEBUG", "CHECKING: Loc: [" + normLocal + "] -> Found on PC? " + foundInWindows);

                    if (!foundInWindows) {
                        File staleFile = new File(baseTargetDir, normLocal);
                        android.util.Log.e("MIRROR_DEBUG", ">>> TRIGGERING DELETE: " + staleFile.getAbsolutePath() 
                                + " | Exists: " + staleFile.exists() + " | IsFile: " + staleFile.isFile());
                        if (staleFile.exists() && staleFile.isFile()) {
                            boolean deleted = staleFile.delete();
                            android.util.Log.e("MIRROR_DEBUG", ">>> DELETION RESULT: " + deleted + " for " + staleFile.getAbsolutePath());
                            if (deleted) {
                                localManifest.remove(localRelPath);
                                deletedCount++;
                            }
                        }
                    } else {
                        //android.util.Log.e("MIRROR_DEBUG", "KEEPING: [" + normLocal + "] matches Windows manifest key [" + winCaseMap.get(normLocal) + "]");
                    }
                }

                if (deletedCount > 0) {
                    pruneEmptyDirectories(baseTargetDir);
                    MediaScannerConnection.scanFile(
                        this,
                        new String[]{ baseTargetDir.getAbsolutePath() },
                        null,
                        null
                    );
                }
            } else {
                //android.util.Log.e("MIRROR_DEBUG", "PRUNING BYPASSED: mirrorExact=" + mirrorExact + ", winCaseMapSize=" + winCaseMap.size());
            }
            //android.util.Log.e("MIRROR_DEBUG", "==============================================================");

            for (Map.Entry<String, String> entry : winCaseMap.entrySet()) {
                String normWinKey = entry.getKey();
                String rawWinKey = entry.getValue();
                long expectedSize = winSizeMap.get(normWinKey);

                Long existingSize = null;
                for (Map.Entry<String, Long> localEntry : localManifest.entrySet()) {
                    if (localEntry.getKey().equalsIgnoreCase(normWinKey)) {
                        existingSize = localEntry.getValue();
                        break;
                    }
                }

                if (existingSize == null || existingSize != expectedSize) {
                    neededArr.put(rawWinKey);
                }
            }

            report.put("needed", neededArr);
            report.put("local_count", localManifest.size());
            report.put("remote_count", winManifest.length());
            report.put("deleted_count", deletedCount);

            Intent auditIntent = new Intent(ACTION_MANIFEST_VERIFIED);
            auditIntent.putExtra("folder_id", folderId);
            auditIntent.putExtra("raw_manifest", rawPrettyManifest);
            auditIntent.putExtra("local_count", localManifest.size());
            auditIntent.putExtra("remote_count", winManifest.length());
            auditIntent.putExtra("deleted_count", deletedCount);
            auditIntent.putExtra("needed_count", neededArr.length());
            auditIntent.setPackage(getPackageName());
            sendBroadcast(auditIntent);

        } catch (Exception e) {
            try {
                report.put("needed", new JSONArray());
                report.put("error", e.getMessage());
            } catch (Exception ignored) {}
        }
        return report;
    }

    private void pruneEmptyDirectories(File dir) {
        if (!dir.isDirectory()) return;
        File[] children = dir.listFiles();
        if (children != null) {
            for (File child : children) {
                if (child.isDirectory()) {
                    pruneEmptyDirectories(child);
                }
            }
        }
        File[] remaining = dir.listFiles();
        if (remaining != null && remaining.length == 0) {
            dir.delete();
        }
    }

    private void scanDirectoryRecursively(File root, File current, Map<String, Long> outMap) {
        File[] files = current.listFiles();
        if (files == null) return;
        for (File f : files) {
            if (f.getName().endsWith(".tmp") || f.getName().endsWith(".upload_tmp")) continue;
            if (f.isDirectory()) {
                scanDirectoryRecursively(root, f, outMap);
            } else {
                String rel = getRelativePath(root, f);
                if (!rel.isEmpty()) {
                    outMap.put(rel, f.length());
                }
            }
        }
    }

    private void deleteRecursive(File fileOrDirectory) {
        if (fileOrDirectory.isDirectory()) {
            File[] children = fileOrDirectory.listFiles();
            if (children != null) {
                for (File child : children) {
                    deleteRecursive(child);
                }
            }
        }
        fileOrDirectory.delete();
    }

    private void scanFileWithMediaScanner(File file) {
        MediaScannerConnection.scanFile(this, new String[]{ file.getAbsolutePath() }, null, null);
    }

    private void rescanMediaStorePath(final String absolutePath) {
        MediaScannerConnection.scanFile(
            this,
            new String[]{ absolutePath },
            null,
            null
        );
    }

    private void broadcastStatus(String msg) {
        Intent intent = new Intent(ACTION_STATUS_UPDATE);
        intent.putExtra("status_msg", msg);
        intent.setPackage(getPackageName());
        sendBroadcast(intent);
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Mirror Sync TCP Engine",
                    NotificationManager.IMPORTANCE_LOW
            );
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) manager.createNotificationChannel(channel);
        }
    }

    private Notification buildNotification(String content) {
        Notification.Builder builder = (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        return builder
                .setContentTitle("Mirror Sync Active")
                .setContentText(content)
                .setSmallIcon(R.drawable.ic_launcher)
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        if (notificationManager != null) {
            notificationManager.notify(NOTIF_ID, buildNotification(text));
        }
    }

    @Override
    public void onDestroy() {
        isRunning = false;
        activeListener = null;
        releaseTransferWakeLock();
        if (serverSocket != null) {
            try { serverSocket.close(); } catch (Exception ignored) {}
        }
        if (multicastLock != null && multicastLock.isHeld()) {
            multicastLock.release();
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}