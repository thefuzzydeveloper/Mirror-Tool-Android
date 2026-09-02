#include <jni.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
#include <fcntl.h>

#define CHUNK_SIZE 1048576

static int delete_recursive(const char *path) {
    struct stat st;
    if (lstat(path, &st) != 0) return -1;

    if (S_ISDIR(st.st_mode)) {
        DIR *dir = opendir(path);
        if (!dir) return -1;
        struct dirent *entry;
        while ((entry = readdir(dir)) != NULL) {
            if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;
            char subpath[2048];
            snprintf(subpath, sizeof(subpath), "%s/%s", path, entry->d_name);
            delete_recursive(subpath);
        }
        closedir(dir);
        return rmdir(path);
    } else {
        return unlink(path);
    }
}

static void prune_hierarchy(const char *src_dir, const char *dst_dir, int current_depth, int max_depth, int mirror_exact) {
    DIR *dir = opendir(dst_dir);
    if (!dir) return;

    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;

        char src_path[2048];
        char dst_path[2048];
        snprintf(src_path, sizeof(src_path), "%s/%s", src_dir, entry->d_name);
        snprintf(dst_path, sizeof(dst_path), "%s/%s", dst_dir, entry->d_name);

        struct stat st_src, st_dst;
        if (lstat(dst_path, &st_dst) != 0) continue;

        int is_dst_dir = S_ISDIR(st_dst.st_mode);

        if (max_depth > 0 && current_depth >= max_depth && is_dst_dir) {
            delete_recursive(dst_path);
            continue;
        }

        int src_exists = (lstat(src_path, &st_src) == 0);
        if (!src_exists) {
            if (mirror_exact || (max_depth > 0 && is_dst_dir)) {
                delete_recursive(dst_path);
            }
        } else {
            int is_src_dir = S_ISDIR(st_src.st_mode);
            if (is_dst_dir != is_src_dir) {
                delete_recursive(dst_path);
            } else if (is_dst_dir) {
                prune_hierarchy(src_path, dst_path, current_depth + 1, max_depth, mirror_exact);
            }
        }
    }
    closedir(dir);
}

static int stream_copy_file(const char *src, const char *dst) {
    int in_fd = open(src, O_RDONLY);
    if (in_fd < 0) return -1;

    int out_fd = open(dst, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out_fd < 0) {
        close(in_fd);
        return -1;
    }

    char *buf = (char *)malloc(CHUNK_SIZE);
    if (!buf) {
        close(in_fd);
        close(out_fd);
        return -1;
    }

    ssize_t bytes;
    while ((bytes = read(in_fd, buf, CHUNK_SIZE)) > 0) {
        ssize_t written = 0;
        while (written < bytes) {
            ssize_t res = write(out_fd, buf + written, bytes - written);
            if (res <= 0) {
                free(buf);
                close(in_fd);
                close(out_fd);
                return -1;
            }
            written += res;
        }
    }

    free(buf);
    close(in_fd);
    close(out_fd);
    return 0;
}

static int mirror_hierarchy(const char *src_dir, const char *dst_dir, int current_depth, int max_depth) {
    DIR *dir = opendir(src_dir);
    if (!dir) return -1;

    mkdir(dst_dir, 0755);

    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;

        char src_path[2048];
        char dst_path[2048];
        snprintf(src_path, sizeof(src_path), "%s/%s", src_dir, entry->d_name);
        snprintf(dst_path, sizeof(dst_path), "%s/%s", dst_dir, entry->d_name);

        struct stat st_src, st_dst;
        if (lstat(src_path, &st_src) == 0) {
            if (S_ISDIR(st_src.st_mode)) {
                if (lstat(dst_path, &st_dst) == 0 && !S_ISDIR(st_dst.st_mode)) {
                    unlink(dst_path);
                }
                mirror_hierarchy(src_path, dst_path, current_depth + 1, max_depth);
            } else {
                if (lstat(dst_path, &st_dst) == 0 && S_ISDIR(st_dst.st_mode)) {
                    delete_recursive(dst_path);
                }

                if (lstat(dst_path, &st_dst) != 0 || st_dst.st_size != st_src.st_size) {
                    stream_copy_file(src_path, dst_path);
                }
            }
        }
    }
    closedir(dir);
    return 0;
}

JNIEXPORT jint JNICALL
Java_com_example_mirror_SyncService_syncDirectoryNative(
    JNIEnv *env,
    jobject thiz,
    jstring src,
    jstring dst,
    jboolean mirror_exact,
    jint scrub_level
) {
    const char *src_path = (*env)->GetStringUTFChars(env, src, NULL);
    const char *dst_path = (*env)->GetStringUTFChars(env, dst, NULL);

    struct stat st;
    if (lstat(src_path, &st) != 0 || !S_ISDIR(st.st_mode)) {
        (*env)->ReleaseStringUTFChars(env, src, src_path);
        (*env)->ReleaseStringUTFChars(env, dst, dst_path);
        return -1;
    }

    prune_hierarchy(src_path, dst_path, 0, scrub_level, mirror_exact);
    int res = mirror_hierarchy(src_path, dst_path, 0, scrub_level);

    (*env)->ReleaseStringUTFChars(env, src, src_path);
    (*env)->ReleaseStringUTFChars(env, dst, dst_path);
    return res;
}