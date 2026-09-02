import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

os.system("")

RESET = "\033[0m"
BOLD = "\033[1m"
GREEN = "\033[92m"
RED = "\033[91m"
CYAN = "\033[96m"

# ==============================================================================
# CONFIGURATION
# ==============================================================================
EXPORT_ROOT = Path(r"F:\Gaming\Godot\Requirements\AndroidExport")
SDK_ROOT = EXPORT_ROOT / "sdk"
JAVA_HOME = EXPORT_ROOT / "java"
JAVA_BIN = JAVA_HOME / "bin"

os.environ["JAVA_HOME"] = str(JAVA_HOME)
os.environ["PATH"] = f"{JAVA_BIN};{os.environ.get('PATH', '')}"

NDK_BIN_DIR = (
    SDK_ROOT / r"ndk\23.2.8568313\toolchains\llvm\prebuilt\windows-x86_64\bin"
)
NDK_CLANG = NDK_BIN_DIR / "aarch64-linux-android28-clang.cmd"

PROJECT_ROOT = Path(__file__).resolve().parent
BUILD_DIR = PROJECT_ROOT / "build"
SRC_DIR = PROJECT_ROOT / "src"
RES_DIR = PROJECT_ROOT / "res"
JNI_DIR = PROJECT_ROOT / "jni"
MANIFEST_FILE = PROJECT_ROOT / "AndroidManifest.xml"
KEYSTORE_FILE = BUILD_DIR / "debug.keystore"

PACKAGE_NAME = "com.example.mirror"
MAIN_ACTIVITY = f"{PACKAGE_NAME}/.MainActivity"
TARGET_ABI = "arm64-v8a"


def log_info(msg: str):
    print(f"{CYAN}[INFO]{RESET} {msg}")


def log_error(msg: str):
    print(f"{RED}{BOLD}[ERROR]{RESET} {msg}")
    sys.exit(1)


def run_command(command, description: str):
    log_info(description)
    is_cmd = isinstance(command, list) and str(command[0]).endswith((".cmd", ".bat"))
    result = subprocess.run(
        command,
        shell=True if (isinstance(command, str) or is_cmd) else False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        log_error(f"Failed: {description}\nSTDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}")


def resolve_sdk_and_jdk_tools():
    javac_exe = JAVA_BIN / "javac.exe"
    keytool_exe = JAVA_BIN / "keytool.exe"
    java_exe = JAVA_BIN / "java.exe"

    for tool in [javac_exe, keytool_exe, java_exe]:
        if not tool.exists():
            log_error(f"Required JDK binary not found at: {tool}")

    build_tools_root = SDK_ROOT / "build-tools"
    versions = sorted(build_tools_root.glob("*"), reverse=True)
    if not versions:
        log_error(f"No Android build-tools found in {build_tools_root}")
    bt_dir = versions[0]

    platforms_root = SDK_ROOT / "platforms"
    platforms = sorted(platforms_root.glob("android-*"), reverse=True)
    if not platforms:
        log_error(f"No Android platforms found in {platforms_root}")
    android_jar = platforms[0] / "android.jar"

    return {
        "javac": javac_exe,
        "keytool": keytool_exe,
        "aapt2": bt_dir / "aapt2.exe",
        "d8": bt_dir / "d8.bat",
        "zipalign": bt_dir / "zipalign.exe",
        "apksigner": bt_dir / "apksigner.bat",
        "android_jar": android_jar,
    }


def build_jni_shared_lib() -> Path:
    lib_dir = BUILD_DIR / "lib" / TARGET_ABI
    lib_dir.mkdir(parents=True, exist_ok=True)
    so_out = lib_dir / "libmirror.so"
    c_source = JNI_DIR / "mirror.c"

    if not c_source.exists():
        log_error(f"Native source missing: {c_source}")

    cmd = [
        str(NDK_CLANG),
        "-shared",
        "-fPIC",
        "-O3",
        str(c_source),
        "-llog",
        "-o",
        str(so_out),
    ]
    run_command(cmd, "Compiling libmirror.so (JNI)...")
    return so_out


def compile_resources_and_link(tools: dict) -> Path:
    compiled_res = BUILD_DIR / "compiled_res.zip"
    gen_dir = BUILD_DIR / "gen"
    gen_dir.mkdir(parents=True, exist_ok=True)
    unaligned_apk = BUILD_DIR / "app-unaligned.apk"

    aapt2_compile_cmd = [
        str(tools["aapt2"]),
        "compile",
        "--dir",
        str(RES_DIR),
        "-o",
        str(compiled_res),
    ]
    run_command(aapt2_compile_cmd, "Compiling resources with aapt2...")

    aapt2_link_cmd = [
        str(tools["aapt2"]),
        "link",
        "-I",
        str(tools["android_jar"]),
        "--manifest",
        str(MANIFEST_FILE),
        "--min-sdk-version",
        "24",
        "--target-sdk-version",
        "34",
        "--java",
        str(gen_dir),
        "--auto-add-overlay",
        "-o",
        str(unaligned_apk),
        str(compiled_res),
    ]
    run_command(aapt2_link_cmd, "Linking resources & generating R.java...")
    return unaligned_apk


def build_dex(tools: dict):
    classes_dir = BUILD_DIR / "classes"
    classes_dir.mkdir(parents=True, exist_ok=True)

    java_files = list(SRC_DIR.rglob("*.java")) + list((BUILD_DIR / "gen").rglob("*.java"))
    
    javac_cmd = [
        str(tools["javac"]),
        "-source",
        "1.8",
        "-target",
        "1.8",
        "-cp",
        str(tools["android_jar"]),
        "-d",
        str(classes_dir),
    ] + [str(f) for f in java_files]
    run_command(javac_cmd, "Compiling Java sources with javac...")

    class_files = list(classes_dir.rglob("*.class"))
    d8_cmd = [
        str(tools["d8"]),
        "--lib",
        str(tools["android_jar"]),
        "--output",
        str(BUILD_DIR),
    ] + [str(f) for f in class_files]
    run_command(d8_cmd, "Generating classes.dex with d8...")


def package_and_sign(tools: dict, unaligned_apk: Path) -> Path:
    aligned_apk = BUILD_DIR / "app-aligned.apk"
    final_apk = BUILD_DIR / "MirrorSync.apk"

    for f in [aligned_apk, final_apk]:
        if f.exists():
            f.unlink()

    log_info("Injecting classes.dex and native libraries into APK package...")
    with zipfile.ZipFile(unaligned_apk, "a", compression=zipfile.ZIP_DEFLATED) as z:
        z.write(BUILD_DIR / "classes.dex", "classes.dex")
        so_path = BUILD_DIR / "lib" / TARGET_ABI / "libmirror.so"
        z.write(so_path, f"lib/{TARGET_ABI}/libmirror.so")

    run_command(
        [
            str(tools["zipalign"]),
            "-f",
            "-p",
            "4",
            str(unaligned_apk),
            str(aligned_apk),
        ],
        "Aligning APK (zipalign)...",
    )

    if not KEYSTORE_FILE.exists():
        keytool_cmd = [
            str(tools["keytool"]),
            "-genkeypair",
            "-v",
            "-keystore",
            str(KEYSTORE_FILE),
            "-alias",
            "androiddebugkey",
            "-keyalg",
            "RSA",
            "-keysize",
            "2048",
            "-validity",
            "10000",
            "-storepass",
            "android",
            "-keypass",
            "android",
            "-dname",
            "CN=Mirror Debug,O=Mirror,C=US",
        ]
        run_command(keytool_cmd, "Generating debug.keystore...")

    run_command(
        [
            str(tools["apksigner"]),
            "sign",
            "--ks",
            str(KEYSTORE_FILE),
            "--ks-pass",
            "pass:android",
            "--ks-key-alias",
            "androiddebugkey",
            "--key-pass",
            "pass:android",
            "--out",
            str(final_apk),
            str(aligned_apk),
        ],
        "Signing APK with apksigner...",
    )
    return final_apk


def deploy(apk: Path):
    run_command(
        ["adb", "install", "-r", str(apk)],
        "Installing APK to connected device...",
    )
    run_command(
        [
            "adb",
            "shell",
            "am",
            "start",
            "-n",
            MAIN_ACTIVITY,
        ],
        f"Launching {MAIN_ACTIVITY}...",
    )


def main():
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    tools = resolve_sdk_and_jdk_tools()
    build_jni_shared_lib()
    unaligned_apk = compile_resources_and_link(tools)
    build_dex(tools)
    final_apk = package_and_sign(tools, unaligned_apk)
    deploy(final_apk)
    print(f"\n{GREEN}{BOLD}MULTI-FOLDER MIRROR SYNC DEPLOYED: {final_apk}{RESET}\n")


if __name__ == "__main__":
    main()