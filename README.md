# Mirror Tool (Wi-Fi Auto Stream Sync)

High-performance, bidirectional Wi-Fi file synchronization and wireless device explorer between Windows 10/11 and Android.

---

## Downloads

* **Windows Host Application (.NET 8):** [Download Mirror.Tool.zip (v2.0)](https://github.com/thefuzzydeveloper/Mirror-Tool-Android/releases/download/v2.0/Mirror.Tool.zip)
* **Android Receiver App:** [Download MirrorSync.apk (v2.0)](https://github.com/thefuzzydeveloper/Mirror-Tool-Android/releases/download/v2.0/MirrorSync.apk)


---

## Core Features

* **Real-Time Directory Monitoring:** Windows `FileSystemWatcher` engine tracks file additions, modifications, renames, and deletions, debouncing updates and synchronizing immediately.
* **Selective Delta Sync:** Compares size and path manifests between Windows and Android to stream only new or modified files over binary TCP streams.
* **Exact Mirror Mode:** Optional configuration per folder pair to delete stale Android files when removed from Windows, keeping directories strictly identical.
* **Folder Scrubbing:** Flatten deep nested directories into customizable single-level structures using configurable scrub levels.
* **Zero-Config Discovery:** UDP beaconing on port `58423` allows Windows and Android devices to locate each other automatically on local subnets.
* **Remote Android File Explorer:** Full Windows UI to inspect internal storage, download files/folders directly to PC, push new files, create directories, and delete items remotely.
* **Battery-Aware Service:** Android background foreground service uses wake-locks during active socket transfers and releases them immediately upon completion.

---

## Network Architecture & Ports

Communication occurs directly over the local network via custom packet frames:

| Port | Protocol | Purpose |
| --- | --- | --- |
| **`58421`** | **TCP** | High-throughput data streaming, remote file operations, and manifest negotiation |
| **`58422`** | **HTTP** | Manifest inspection, snapshot polling, and manual sync triggers |
| **`58423`** | **UDP** | Broadcast auto-discovery beacon between PC and Android devices |

---

## Quick Start Guide

### 1. Android Device Setup

1. Install `MirrorSync.apk` on your Android device running Android 7.0 (API 24) or later.
2. Open the app and grant **All Files Access** (`MANAGE_EXTERNAL_STORAGE`) and notification permissions.
3. Ensure your device is connected to the same Wi-Fi network as your PC. The app displays your device IP address and port `58421`.

### 2. Windows PC Setup

1. Extract `Mirror.Tool.zip` and run `WiFiAutoStreamSync.exe`.
2. A tray icon will appear indicating discovery status.
3. Right-click the system tray icon and select **Configure Sync Folders...**:
* Click **+ Add Folder...** to choose local source folders.
* Set file extension filters (e.g., `*` for all, or `.docx, .pdf`).
* Set the folder scrubbing level (0 keeps the original directory tree).
* Click **Save & Broadcast** to push the configuration to the Android device.

### 3. Folder Mapping & Sync

1. On the Android app, tap **🔄 Fetch Pairs** (or wait for the auto-discovery beacon).
2. For each folder card, review or choose your preferred destination on the device storage.
3. Enable **Mirror exactly** if you want files removed on Windows to automatically delete from Android.
4. Any change inside your Windows source directory will automatically stream to the device.

---

## Using the Remote Device Explorer

Right-click the Windows system tray icon and select **Browse Android Devices & Storage...** to open the wireless browser:

* Browse internal storage (`/storage/emulated/0`) and synced application roots in real time.
* **Save File / Folder to PC:** Download files or entire directory trees from your phone to any Windows directory.
* **Upload File / Folder:** Push files and folders directly to Android.
* **Open / Preview:** Temporarily pull and launch files directly on Windows.

---

## Technical Specifications

* **Windows Platform:** C# / .NET 8 (Windows Forms), asynchronous sockets (`System.Net.Sockets`), HTTP listener (`HttpListener`).
* **Android Platform:** Java / Native JNI C integration (`libnative-sync.so`), Android Foreground Service, MediaScanner synchronization.
* **Payload Chunking:** 4 MB streaming buffer chunks with 16-bit binary headers for payload delivery.
Remember - mirroring android folders to PC is not feasible due to battery restrictions (it is possible, however not feasible)




## Mirror Tool: High-Performance Wireless Sync & Storage Gateway

Most cross-platform synchronization systems force compromises: high battery drain from endless polling, throttled cloud servers, or manual approval dialogs for every transfer. **Mirror Tool** replaces cloud friction with an automated, ultra-fast local network bridge connecting Windows PCs and Android devices with zero cloud reliance.

|**Capability**|**Mirror Tool**|**Cloud Storage (Drive/Dropbox)**|**Ad-Hoc Apps (LocalSend/AirDroid)**|**Continuous Sync (Syncthing)**|
|---|---|---|---|---|
|**Network Architecture**|**100% Local Wi-Fi (No Internet)**|WAN / External Cloud|Local Wi-Fi (Manual Prompt)|Local Mesh / Public Relays|
|**Sync Strategy**|**Event-Driven & Sleeping (Zero CPU)**|Background Cloud Polling|Manual Trigger Only|Continuous Interval Scans|
|**Transfer Engine**|**Raw TCP with 4MB Memory Chunks**|HTTP Chunking / Rate-Limited|WebSockets / HTTP REST|Block-level Protocol (BEP)|
|**Deletions & Mirroring**|**Native C Deep Prune Engine**|Cloud Trash Can Retention|Overwrite Only (No Pruning)|Database Index Pruning|
|**Explorer Interface**|**Native Windows UI (No Browser)**|Virtual Web Shell|Browser Web Portal|Web GUI Dashboard|
|**Network Discovery**|**On-Demand UDP (5-Min Auto-Off)**|Account Credentials|Manual PIN / Broadcast|Cryptographic Device IDs|
|**Directory Scrubbing**|**Configurable Tree-Depth Flattening**|Unsupported|Unsupported|Unsupported|

### Core Architecture & Deep Technical Features

**1. Event-Driven Zero-CPU Synchronization Engine**

- **Instant Detection via FileSystemWatcher:** Changes to watched Windows directories—including file creation, modification, renaming, or deletion—trigger immediate background synchronization without polling intervals.
- **Intelligent Debounce Layer:** Fast, consecutive write operations are collected and processed efficiently via cancellation-backed timers, eliminating redundant stream bursts.
- **True Standby Efficiency:** The engine executes entirely on-demand. Sockets wait passively on system events, allowing phone CPU and PC cores to drop to 0% utilization while idle.

**2. High-Throughput Binary Protocol (Port 58421)**

- **Uncapped Streaming:** Uses raw, low-overhead binary TCP sockets using a persistent two-byte magic header (`0xAA`, `0x55`).
- **4MB Array-Pooled Buffer Slices:** Transfers use rented 4MB memory chunks (`Protocol.ChunkStreamSize`), minimizing garbage collection pressure and saturating maximum Wi-Fi bandwidth.
- **Atomic File Ingestion:** Android files arrive as temporary staging buffers (`.tmp`) before moving atomically into final target directories to prevent half-written files.
- **Automated System Media Indexing:** Android's internal `MediaScannerConnection` is notified after every transfer and deletion, making media visible instantly in gallery and player apps without restarting your device.

**3. Native C-Level Bi-Directional Mirroring & Pruning**

- **Native Performance:** Core traversal relies on optimized native C code (`mirror.c`) via JNI for high-speed file scrubbing and directory cleanup.
- **Exact Synchronization (`mirrorExact`):** Files deleted or modified on the host PC can be cleaned up automatically on Android, preventing storage bloat.
- **Configurable Tree Scrubbing:** Flattens deeply nested PC directories into neat, readable structures on mobile according to custom scrub levels.
- **Granular Extension Filters:** Configure specific file whitelist and blacklist rules (e.g., sync only `.pdf`, `.mp4`, or ignore `.tmp`, `.log`) per sync directory.

**4. Built-in Desktop Device & Storage Explorer**

- **Zero Browser Overhead:** Browse Android storage (`/storage/emulated/0`) directly from a native Windows interface, bypassing ad-supported web portals.
- **Bi-Directional File Management:** Upload desktop documents, download entire mobile directories to PC, inspect contents, or trigger remote deletions directly.
- **Live Manifest Diagnostics:** Inspect live remote file indices and transfer queues with a single click to audit sync accuracy.

**5. Intelligent On-Demand Pairing with 5-Minute Auto-Timeout**

- **Smart UDP Beaconing (Port 58423):** Broadcasts announcements across local subnets automatically, pairing devices instantly without manual IP entry.
- **Battery-Saving 5-Minute Timer:** Automatic network discovery deactivates after 5 minutes, preventing background Wi-Fi chatter and saving power.
- **Persistent Peer Reconnection:** Known client IPs are saved locally, maintaining rapid automated connections on demand even when discovery is disabled.
- **Manual Target Control:** Dedicated override controls let you lock down specific static IPs for complex multi-network setups.

### Step-by-Step Quick Start Guide

**Step 1: Set Up Storage Folders on PC**

1. Right-click the system tray icon and open **Configure Sync Folders...**.
2. Click **+ Add Folder**, browse to your folder, and define allowed file extensions (or `*` for all files).
3. Choose a **Folder Scrubbing Level** if you want deep subdirectories flattened on your phone, then click **Apply**.

**Step 2: Connect the Android Device**

1. Ensure both devices are connected to the same Wi-Fi network.
2. Launch the Android companion app. The app automatically broadcasts on UDP port 58423 to locate the PC.
3. The PC tray menu will show pairing discovery as active (automatically toggling off after 5 minutes to conserve power).

**Step 3: Browse Storage and Verify Sync**

1. Open the tray menu and select **Browse Android Devices & Storage...** to explore mobile folders.
2. Tap **Verify & Sync** in the Android app or **Sync All Folders Now** from the PC tray to trigger an instant delta sync check.
