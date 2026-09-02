# Mirror Tool (Wi-Fi Auto Stream Sync)

High-performance, bidirectional Wi-Fi file synchronization and wireless device explorer between Windows 10/11 and Android.

---

## Downloads

* **Windows Host Application (.NET 8):** [Download Mirror.Tool.zip (v1.5)](https://github.com/thefuzzydeveloper/Mirror-Tool-Android/releases/download/v1.5/Mirror.Tool.zip)
* **Android Receiver App:** [Download MirrorSync.apk (v1.5)](https://github.com/thefuzzydeveloper/Mirror-Tool-Android/releases/download/v1.5/MirrorSync.apk)


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
