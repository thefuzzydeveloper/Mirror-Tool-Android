// android_mirror/src/com/example/mirror/MainActivity.java
package com.example.mirror;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.DocumentsContract;
import android.provider.Settings;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.CompoundButton;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.util.*;

public class MainActivity extends Activity implements SyncService.ConfigUpdateListener{
    private static final String PREFS_NAME = "FolderMappingsPrefs";
    private static final int REQ_MANAGE_STORAGE = 1001;
    private static final int REQ_PICK_FOLDER = 1002;
    private static final int HTTP_MANIFEST_PORT = 58422;

    private LinearLayout containerFolderPairs;
    private TextView tvServiceStatus;
    private TextView tvIpAddress;
    private EditText etPcIpOverride;
    private CheckBox chkAllowDeletions;
    private TextView tvDeletionPin;
    private SharedPreferences prefs;
    private String activePickingFolderId = null;
    private final Map<String, TextView> manifestStatusViews = new HashMap<>();
    private final Map<String, String> lastManifestDump = new HashMap<>();
    private boolean isPromptShowing = false;

    private final BroadcastReceiver statusReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (intent == null || intent.getAction() == null) return;
            if (SyncService.ACTION_STATUS_UPDATE.equals(intent.getAction())) {
                final String msg = intent.getStringExtra("status_msg");
                if (msg != null) {
                    runOnUiThread(() -> {
                        tvServiceStatus.setText("Status: " + msg);
                        if (msg.startsWith("Receiving:")) {
                            tvServiceStatus.setTextColor(Color.parseColor("#38BDF8"));
                        } else if (msg.contains("Sync Completed") || msg.contains("Online") || msg.contains("Connected")) {tvServiceStatus.setTextColor(Color.parseColor("#4ADE80"));}
                    });
                }
            } else if (SyncService.ACTION_PC_DISCOVERED.equals(intent.getAction())) {
                final String pcIp = intent.getStringExtra("pc_ip");
                if (pcIp != null && etPcIpOverride != null) {
                    runOnUiThread(() -> {
                        if (!pcIp.equals(etPcIpOverride.getText().toString().trim())) {etPcIpOverride.setText(pcIp);}
                    });
                }
            } else if (SyncService.ACTION_DELETION_LOCKED.equals(intent.getAction())) {
                runOnUiThread(() -> {
                    if (chkAllowDeletions != null) chkAllowDeletions.setChecked(false);
                    if (tvDeletionPin != null) {
                        tvDeletionPin.setText(" [PIN: LOCKED]");
                        tvDeletionPin.setTextColor(Color.parseColor("#FCA5A5"));
                    }
                });
            } else if (SyncService.ACTION_CONFIG_REFRESH.equals(intent.getAction())) {
                runOnUiThread(MainActivity.this::readSourcesFromFile);
            } else if (SyncService.ACTION_MANIFEST_VERIFIED.equals(intent.getAction())) {
                final String folderId = intent.getStringExtra("folder_id");
                final String statusNote = intent.getStringExtra("status_note");
                final String rawManifest = intent.getStringExtra("raw_manifest");
                final int localCount = intent.getIntExtra("local_count", 0);
                final int remoteCount = intent.getIntExtra("remote_count", 0);
                final int deletedCount = intent.getIntExtra("deleted_count", 0);
                final int neededCount = intent.getIntExtra("needed_count", 0);
                if (rawManifest != null) {lastManifestDump.put(folderId, rawManifest);}

                runOnUiThread(() -> {
                    TextView tvManifest = manifestStatusViews.get(folderId);
                    if (tvManifest != null) {
                        if (statusNote != null) {
                            tvManifest.setText("Manifest: " + statusNote);
                            tvManifest.setTextColor(Color.parseColor("#94A3B8"));
                        } else {
                            tvManifest.setText("Manifest: " + remoteCount + " on PC | " + localCount + " local | " + deletedCount + " pruned | " + neededCount + " transferring (Tap to View Raw)");
                            tvManifest.setTextColor(Color.parseColor("#38BDF8"));
                        }
                    }
                });
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        containerFolderPairs = findViewById(R.id.containerFolderPairs);
        tvServiceStatus = findViewById(R.id.tvServiceStatus);
        tvIpAddress = findViewById(R.id.tvIpAddress);
        setupTopControlBar();
        checkPermissions();
        updateIpDisplay();
        IntentFilter filter = new IntentFilter();
        filter.addAction(SyncService.ACTION_STATUS_UPDATE);
        filter.addAction(SyncService.ACTION_CONFIG_REFRESH);
        filter.addAction(SyncService.ACTION_MANIFEST_VERIFIED);
        filter.addAction(SyncService.ACTION_PC_DISCOVERED);
        filter.addAction(SyncService.ACTION_DELETION_LOCKED);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(statusReceiver, filter, Context.RECEIVER_EXPORTED);
        } else {registerReceiver(statusReceiver, filter);}
        SyncService.activeListener = this;
        startSyncService();
        readSourcesFromFile();
        SyncService.broadcastPresenceToPc(this);
    }

    @Override
    protected void onResume() {
        super.onResume();
        SyncService.broadcastPresenceToPc(this);
        File configFile = new File(getFilesDir(), "windows_sources.json");
        if (!configFile.exists() || configFile.length() == 0) {
            requestFolderConfiguration();
        } else {
            triggerPcStream(getEffectivePcIp());
        }
    }

    private void setupTopControlBar() {
        ViewParent parent = containerFolderPairs.getParent();
        if (parent == null) return;
        ViewGroup targetParent = null;
        int insertIndex = -1;
        if (parent instanceof ScrollView) {
            ViewParent grandParent = ((ScrollView) parent).getParent();
            if (grandParent instanceof ViewGroup) {
                targetParent = (ViewGroup) grandParent;
                insertIndex = targetParent.indexOfChild((View) parent);
            }
        } else if (parent instanceof ViewGroup) {
            targetParent = (ViewGroup) parent;
            insertIndex = targetParent.indexOfChild(containerFolderPairs);
        }
        if (targetParent == null) return;
        LinearLayout controlCard = new LinearLayout(this);
        controlCard.setOrientation(LinearLayout.VERTICAL);
        controlCard.setPadding(16, 16, 16, 16);
        controlCard.setBackgroundColor(Color.parseColor("#0F172A"));
        ViewGroup.MarginLayoutParams lp = new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 8, 0, 14);
        controlCard.setLayoutParams(lp);
        LinearLayout pcIpRow = new LinearLayout(this);
        pcIpRow.setOrientation(LinearLayout.HORIZONTAL);
        TextView tvLabel = new TextView(this);
        tvLabel.setText("Windows PC IP: ");
        tvLabel.setTextColor(Color.parseColor("#38BDF8"));
        tvLabel.setTypeface(null, Typeface.BOLD);
        tvLabel.setTextSize(13);
        etPcIpOverride = new EditText(this);
        etPcIpOverride.setText(prefs.getString("last_pc_ip", ""));
        etPcIpOverride.setTextColor(Color.WHITE);
        etPcIpOverride.setHint("e.g. 192.168.1.5");
        etPcIpOverride.setHintTextColor(Color.parseColor("#64748B"));
        etPcIpOverride.setTextSize(13);
        etPcIpOverride.setBackgroundColor(Color.parseColor("#1E293B"));
        etPcIpOverride.setPadding(14, 8, 14, 8);
        LinearLayout.LayoutParams etP = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.0f);
        etPcIpOverride.setLayoutParams(etP);

        etPcIpOverride.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {}
            @Override
            public void afterTextChanged(Editable s) {prefs.edit().putString("last_pc_ip", s.toString().trim()).apply();}
        });
        pcIpRow.addView(tvLabel);
        pcIpRow.addView(etPcIpOverride);
        // Security Barrier for File Deletion
        LinearLayout deletionSecurityRow = new LinearLayout(this);
        deletionSecurityRow.setOrientation(LinearLayout.HORIZONTAL);
        deletionSecurityRow.setPadding(0, 10, 0, 0);

        chkAllowDeletions = new CheckBox(this);
        chkAllowDeletions.setText("Allow Remote Deletions");
        chkAllowDeletions.setTextColor(Color.parseColor("#FCA5A5"));
        chkAllowDeletions.setTextSize(12);

        tvDeletionPin = new TextView(this);
        tvDeletionPin.setText(" [PIN: LOCKED]");
        tvDeletionPin.setTextColor(Color.parseColor("#94A3B8"));
        tvDeletionPin.setTextSize(12);
        tvDeletionPin.setTypeface(null, Typeface.BOLD);

        chkAllowDeletions.setOnCheckedChangeListener((btn, isChecked) -> {
            if (isChecked) {
                int randomPin = 100000 + new Random().nextInt(900000);
                SyncService.setDeletionPin(String.valueOf(randomPin), 300000L);
                tvDeletionPin.setText(" [PIN: " + SyncService.activeDeletionPin + " (5m)]");
                tvDeletionPin.setTextColor(Color.parseColor("#4ADE80"));
                Toast.makeText(this, "Remote deletions UNLOCKED (5 min timeout). PIN: " + SyncService.activeDeletionPin, Toast.LENGTH_LONG).show();
            } else {
                SyncService.setDeletionPin("", 0);
                tvDeletionPin.setText(" [PIN: LOCKED]");
                tvDeletionPin.setTextColor(Color.parseColor("#94A3B8"));
                Toast.makeText(this, "Remote deletions BLOCKED.", Toast.LENGTH_SHORT).show();
            }
        });
        deletionSecurityRow.addView(chkAllowDeletions);
        deletionSecurityRow.addView(tvDeletionPin);
        controlCard.addView(pcIpRow);
        controlCard.addView(deletionSecurityRow);
        if (insertIndex >= 0) {
            targetParent.addView(controlCard, insertIndex);
        } else {targetParent.addView(controlCard);}
    }

    private String getEffectivePcIp() {
        if (etPcIpOverride != null) {
            String typed = etPcIpOverride.getText().toString().trim();
            if (!typed.isEmpty()) return typed;
        }
        return prefs.getString("last_pc_ip", "").trim();
    }

    private void requestFolderConfiguration() {
        final String pcIp = getEffectivePcIp();
        if (pcIp.isEmpty()) {
            SyncService.broadcastPresenceToPc(this);
            Toast.makeText(this, "Enter Windows PC IP above, or wait for auto-discovery beacon.", Toast.LENGTH_SHORT).show();
            return;
        }
        tvServiceStatus.setText("Status: Fetching folder pairs from " + pcIp + "...");
        tvServiceStatus.setTextColor(Color.parseColor("#38BDF8"));

        SyncService.fetchConfigFromPc(this, pcIp, () -> runOnUiThread(() -> {
            readSourcesFromFile();
            Toast.makeText(MainActivity.this, "Folder pairs updated from PC", Toast.LENGTH_SHORT).show();
        }));
    }

    private void downloadAndVerifyAllManifests() {
        final String pcIp = getEffectivePcIp();
        if (pcIp.isEmpty()) {
            SyncService.broadcastPresenceToPc(this);
            Toast.makeText(this, "Enter Windows PC IP above or wait for auto-discovery.", Toast.LENGTH_SHORT).show();
            return;
        }
        tvServiceStatus.setText("Status: Fetching manifest snapshots from " + pcIp + "...");
        tvServiceStatus.setTextColor(Color.parseColor("#38BDF8"));

        new Thread(() -> {
            try {
                URL url = new URL("http://" + pcIp + ":" + HTTP_MANIFEST_PORT + "/manifests");
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setConnectTimeout(4000);
                conn.setReadTimeout(5000);
                conn.setRequestMethod("GET");
                if (conn.getResponseCode() == 200) {
                    BufferedReader in = new BufferedReader(new InputStreamReader(conn.getInputStream(), "UTF-8"));
                    StringBuilder sb = new StringBuilder();
                    String line;
                    while ((line = in.readLine()) != null) sb.append(line);
                    in.close();
                    JSONObject json = new JSONObject(sb.toString());
                    final JSONArray foldersArr = json.getJSONArray("folders");
                    for (int i = 0; i < foldersArr.length(); i++) {
                        JSONObject fObj = foldersArr.getJSONObject(i);
                        final String folderId = fObj.getString("id");
                        final String folderName = fObj.getString("name");
                        JSONObject winManifest = fObj.getJSONObject("manifest");
                        if (winManifest.has("files") && winManifest.optJSONObject("files") != null) {winManifest = winManifest.getJSONObject("files");}
                        String rawPrettyManifest;
                        try {
                            rawPrettyManifest = winManifest.toString(2);
                        } catch (Exception e) {rawPrettyManifest = winManifest.toString();}
                        String defaultTarget = "/storage/emulated/0/" + folderName;
                        String targetDir = prefs.getString(folderId, defaultTarget);
                        boolean isEnabled = prefs.getBoolean(folderId + "_sync_enabled", true);
                        boolean mirrorExact = prefs.getBoolean(folderId + "_mirror_exact", false);
                        if (!isEnabled) {
                            postManifestAuditUi(folderId, "Sync Ignored (Disabled)", rawPrettyManifest);
                            continue;
                        }
                        File targetRoot = new File(targetDir);
                        if (!targetRoot.exists()) targetRoot.mkdirs();
                        Map<String, Long> localMap = new HashMap<>();
                        scanDir(targetRoot, targetRoot, localMap);
                        Map<String, Long> winSizeMap = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
                        Iterator<String> wKeys = winManifest.keys();
                        while (wKeys.hasNext()) {
                            String rawK = wKeys.next();
                            String sanitizedK = SyncService.sanitizeRemotePath(rawK, folderName);
                            if (!sanitizedK.isEmpty()) {winSizeMap.put(sanitizedK, winManifest.getLong(rawK));}
                        }
                        // android.util.Log.e("MIRROR_DEBUG", "========== MAINACTIVITY VERIFY AUDIT ==========");
                        // android.util.Log.e("MIRROR_DEBUG", "TargetRoot: " + targetRoot + " | mirrorExact: " + mirrorExact);
                        // android.util.Log.e("MIRROR_DEBUG", "WIN KEYS COUNT: " + winSizeMap.size() + " | LOCAL KEYS COUNT: " + localMap.size());
                        // for (String wk : winSizeMap.keySet()) {
                        //     android.util.Log.e("MIRROR_DEBUG", "   MAIN_ACT WIN KEY: [" + wk + "]");
                        // }
                        // for (String lk : localMap.keySet()) {
                        //     android.util.Log.e("MIRROR_DEBUG", "   MAIN_ACT LOC KEY: [" + lk + "]");
                        // }
                        int pruned = 0;
                        if (mirrorExact && !winSizeMap.isEmpty() && winManifest.length() > 0) {
                            for (String localRel : new ArrayList<>(localMap.keySet())) {
                                String norm = SyncService.normalizePath(localRel);
                                if (norm.isEmpty() || norm.equals(".")) continue;
                                boolean matches = winSizeMap.containsKey(norm);
                                //android.util.Log.e("MIRROR_DEBUG", "MAIN_ACT CHECK: [" + norm + "] in Win? " + matches);
                                if (!matches) {
                                    File stale = new File(targetRoot, norm);
                                    android.util.Log.e("MIRROR_DEBUG", ">>> MAIN_ACT DELETING: " + stale.getAbsolutePath());
                                    if (stale.exists() && stale.isFile() && !stale.equals(targetRoot)) {
                                        if (stale.delete()) {
                                            localMap.remove(localRel);
                                            pruned++;
                                            android.util.Log.e("MIRROR_DEBUG", ">>> MAIN_ACT DELETED SUCCESS: " + stale.getName());
                                        }
                                    }
                                }
                            }
                        }
                        //android.util.Log.e("MIRROR_DEBUG", "===============================================");
                        int needed = 0;
                        for (Map.Entry<String, Long> entry : winSizeMap.entrySet()) {
                            String wPath = entry.getKey();
                            long wSize = entry.getValue();
                            Long lSize = null;
                            for (Map.Entry<String, Long> lEntry : localMap.entrySet()) {
                                if (lEntry.getKey().equalsIgnoreCase(wPath)) {
                                    lSize = lEntry.getValue();
                                    break;
                                }
                            }
                            if (lSize == null || lSize != wSize) {needed++;}
                        }

                        final String resultText = winManifest.length() + " on PC | " + localMap.size() + " local | " + pruned + " pruned | " + needed + " transferring";
                        postManifestAuditUi(folderId, resultText, rawPrettyManifest);
                    }
                    triggerPcStream(pcIp);
                }
            } catch (final Exception e) {
                runOnUiThread(() -> {
                    tvServiceStatus.setText("Status: Manifest Timeout: " + e.getMessage());
                    tvServiceStatus.setTextColor(Color.parseColor("#FCA5A5"));
                    triggerPcStream(pcIp);
                });
            }
        }).start();
    }

    private void scanDir(File root, File cur, Map<String, Long> map) {
        File[] files = cur.listFiles();
        if (files == null) return;
        for (File f : files) {
            if (f.getName().endsWith(".tmp") || f.getName().endsWith(".upload_tmp")) continue;
            if (f.isDirectory()) {
                scanDir(root, f, map);
            } else {
                String rel = SyncService.getRelativePath(root, f);
                if (!rel.isEmpty()) {map.put(SyncService.normalizePath(rel), f.length());}
            }
        }
    }

    private void postManifestAuditUi(final String folderId, final String text, final String rawManifestJson) {
        runOnUiThread(() -> {
            lastManifestDump.put(folderId, rawManifestJson);
            TextView tv = manifestStatusViews.get(folderId);
            if (tv != null) {
                tv.setText("Manifest: " + text + " (Tap to View Raw)");
                tv.setTextColor(Color.parseColor("#38BDF8"));
            }
        });
    }

    private void showManifestDialog(String folderId, String folderName) {
        String rawManifest = lastManifestDump.get(folderId);
        if (rawManifest == null || rawManifest.trim().isEmpty()) {rawManifest = "No raw manifest received for this folder yet.\n\nTap '⚡ Verify & Sync' or trigger a sync from PC to view the payload.";}
        ScrollView sv = new ScrollView(this);
        TextView tv = new TextView(this);
        tv.setText(rawManifest);
        tv.setTextSize(12);
        tv.setTypeface(Typeface.MONOSPACE);
        tv.setTextColor(Color.parseColor("#E2E8F0"));
        tv.setPadding(30, 30, 30, 30);
        tv.setTextIsSelectable(true);
        sv.addView(tv);
        new AlertDialog.Builder(this).setTitle("Received Manifest File - " + folderName).setView(sv).setPositiveButton("Close", null).show();
    }

    private void triggerPcStream(final String pcIp) {
        if (pcIp == null || pcIp.trim().isEmpty()) return;
        new Thread(() -> {
            try {
                String myIp = getDeviceIpAddress();
                String urlStr = "http://" + pcIp.trim() + ":" + HTTP_MANIFEST_PORT + "/trigger_sync";
                if (myIp != null && !myIp.isEmpty()) {urlStr += "?ip=" + URLEncoder.encode(myIp, "UTF-8");}
                URL url = new URL(urlStr);
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setConnectTimeout(3000);
                conn.setReadTimeout(3000);
                conn.setRequestMethod("GET");
                conn.getResponseCode();
                conn.disconnect();
            } catch (Exception ignored) {}
        }).start();
    }

    private void updateIpDisplay() {
        String ip = getDeviceIpAddress();
        tvIpAddress.setText("Phone Wi-Fi IP: " + (ip != null ? ip : "No Wi-Fi") + " (Port " + SyncService.TCP_DATA_PORT + ")");
    }

    private String getDeviceIpAddress() {
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (nif.isLoopback() || !nif.isUp()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (!addr.isLoopbackAddress() && addr instanceof Inet4Address) {return addr.getHostAddress();}
                }
            }
        } catch (Exception ignored) {}
        return null;
    }

    private void startSyncService() {
        Intent intent = new Intent(this, SyncService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent);
        } else {startService(intent);}
    }

    private void restartSyncService() {
        stopService(new Intent(this, SyncService.class));
        tvServiceStatus.setText("Status: Restarting server...");
        tvServiceStatus.setTextColor(Color.parseColor("#F59E0B"));
        containerFolderPairs.postDelayed(() -> {
            startSyncService();
            updateIpDisplay();
            SyncService.broadcastPresenceToPc(MainActivity.this);
        }, 600);
    }

    @Override
    public void onConfigUpdated(final String jsonConfig) {runOnUiThread(() -> renderWindowsSources(jsonConfig));}

    private void checkPermissions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, 102);
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            if (!Environment.isExternalStorageManager()) {
                try {
                    Intent intent = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION);
                    intent.setData(Uri.parse("package:" + getPackageName()));
                    startActivityForResult(intent, REQ_MANAGE_STORAGE);
                } catch (Exception e) {
                    Intent intent = new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION);
                    startActivityForResult(intent, REQ_MANAGE_STORAGE);
                }
            }
        }
    }

    private void readSourcesFromFile() {
        File file = new File(getFilesDir(), "windows_sources.json");
        if (!file.exists()) {
            showNoSourcesMessage();
            return;
        }
        try (FileInputStream fis = new FileInputStream(file)) {
            byte[] data = new byte[(int) file.length()];
            fis.read(data);
            renderWindowsSources(new String(data, "UTF-8"));
        } catch (Exception e) {showNoSourcesMessage();}
    }

    private void showNoSourcesMessage() {
        containerFolderPairs.removeAllViews();
        TextView empty = new TextView(this);
        empty.setText("Waiting for Windows PC...\nEnter PC IP above or ensure sync script is active.");
        empty.setTextColor(Color.parseColor("#94A3B8"));
        empty.setTextSize(13);
        empty.setPadding(0, 20, 0, 0);
        containerFolderPairs.addView(empty);
    }

    private void promptFolderDestination(final String folderId, final String folderName, final String localPath) {
        if (isPromptShowing) return;
        isPromptShowing = true;
        final String defaultTarget = "/storage/emulated/0/" + folderName;
        new AlertDialog.Builder(this)
            .setTitle("New Folder Pair Discovered")
            .setMessage("Windows Source:\n" + localPath + "\n\nChoose destination on Android:\nDefault: " + defaultTarget)
            .setCancelable(false)
            .setPositiveButton("Use Default", (dialog, which) -> {
                isPromptShowing = false;
                prefs.edit()
                    .putString(folderId, defaultTarget)
                    .putBoolean(folderId + "_confirmed", true)
                    .putBoolean(folderId + "_sync_enabled", true)
                    .apply();
                readSourcesFromFile();
                triggerPcStream(getEffectivePcIp());
            })
            .setNeutralButton("Select Folder...", (dialog, which) -> {
                isPromptShowing = false;
                activePickingFolderId = folderId;
                Intent pickerIntent = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
                startActivityForResult(pickerIntent, REQ_PICK_FOLDER);
            })
            .setNegativeButton("Configure Later", (dialog, which) -> {
                isPromptShowing = false;
            })
            .show();
    }

    private void renderWindowsSources(String jsonStr) {
        containerFolderPairs.removeAllViews();
        manifestStatusViews.clear();
        try {
            JSONArray array = new JSONArray(jsonStr);
            if (array.length() == 0) {
                showNoSourcesMessage();
                return;
            }
            String unconfirmedId = null;
            String unconfirmedName = null;
            String unconfirmedPath = null;

            for (int i = 0; i < array.length(); i++) {
                JSONObject obj = array.getJSONObject(i);
                final String folderId = obj.optString("id");
                final String folderName = obj.optString("name");
                final String localPath = obj.optString("local_path");
                int scrubLevel = obj.optInt("scrub_level", 0);
                String scrubText = (scrubLevel == 0) ? "Tree" : ("Lvl " + scrubLevel);
                JSONArray extsArr = obj.optJSONArray("extensions");
                StringBuilder extsSummary = new StringBuilder();
                if (extsArr != null) {
                    for (int j = 0; j < extsArr.length(); j++) {
                        if (j > 0) extsSummary.append(", ");
                        extsSummary.append(extsArr.getString(j));
                    }
                } else {extsSummary.append("*");}

                final String defaultTarget = "/storage/emulated/0/" + folderName;
                final String savedTarget = prefs.getString(folderId, defaultTarget);
                final boolean isConfirmed = prefs.getBoolean(folderId + "_confirmed", false);
                final boolean isMirrorExact = prefs.getBoolean(folderId + "_mirror_exact", false);
                final boolean isSyncEnabled = prefs.getBoolean(folderId + "_sync_enabled", true) && isConfirmed;

                if (!isConfirmed && unconfirmedId == null) {
                    unconfirmedId = folderId;
                    unconfirmedName = folderName;
                    unconfirmedPath = localPath;
                }

                LinearLayout card = new LinearLayout(this);
                card.setOrientation(LinearLayout.VERTICAL);
                card.setPadding(16, 14, 16, 14);
                card.setBackgroundColor(Color.parseColor("#1E293B"));
                LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
                cardParams.setMargins(0, 0, 0, 12);
                card.setLayoutParams(cardParams);

                LinearLayout headerRow = new LinearLayout(this);
                headerRow.setOrientation(LinearLayout.HORIZONTAL);
                TextView tvTitle = new TextView(this);
                tvTitle.setText(folderName);
                tvTitle.setTextColor(Color.parseColor("#F8FAFC"));
                tvTitle.setTextSize(15);
                tvTitle.setTypeface(null, Typeface.BOLD);
                LinearLayout.LayoutParams nameParams = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.0f);
                tvTitle.setLayoutParams(nameParams);

                final CheckBox chkSync = new CheckBox(this);
                chkSync.setText(isConfirmed ? (isSyncEnabled ? "Active" : "Paused") : "Unset");
                chkSync.setTextColor(isConfirmed && isSyncEnabled ? Color.parseColor("#4ADE80") : Color.parseColor("#94A3B8"));
                chkSync.setTextSize(12);
                chkSync.setChecked(isSyncEnabled);
                chkSync.setEnabled(isConfirmed);

                headerRow.addView(tvTitle);
                headerRow.addView(chkSync);
                card.addView(headerRow);

                TextView tvPaths = new TextView(this);
                String destDisplay = isConfirmed ? savedTarget : "[Tap below to select destination folder]";
                tvPaths.setText("PC: " + localPath + "\nAndroid: " + destDisplay);
                tvPaths.setTextColor(isConfirmed ? Color.parseColor("#94A3B8") : Color.parseColor("#F59E0B"));
                tvPaths.setTextSize(11);
                tvPaths.setPadding(0, 4, 0, 4);
                card.addView(tvPaths);

                TextView tvManifestAudit = new TextView(this);
                tvManifestAudit.setText("Filters: [" + extsSummary + "] | Scrub: " + scrubText + " | Tap for manifest");
                tvManifestAudit.setTextColor(Color.parseColor("#38BDF8"));
                tvManifestAudit.setTextSize(11);
                tvManifestAudit.setPadding(0, 2, 0, 6);
                tvManifestAudit.setOnClickListener(v -> showManifestDialog(folderId, folderName));
                manifestStatusViews.put(folderId, tvManifestAudit);
                card.addView(tvManifestAudit);

                LinearLayout actionsRow = new LinearLayout(this);
                actionsRow.setOrientation(LinearLayout.HORIZONTAL);
                actionsRow.setPadding(0, 4, 0, 0);

                Button btnFolderAction = new Button(this);
                btnFolderAction.setText(isConfirmed ? "📁 Change Destination" : "⚡ Choose Destination");
                btnFolderAction.setTextSize(11);
                btnFolderAction.setTextColor(Color.WHITE);
                btnFolderAction.setBackgroundColor(isConfirmed ? Color.parseColor("#334155") : Color.parseColor("#0284C7"));
                btnFolderAction.setPadding(16, 6, 16, 6);
                btnFolderAction.setOnClickListener(v -> promptFolderDestination(folderId, folderName, localPath));

                final CheckBox chkMirror = new CheckBox(this);
                chkMirror.setText("Mirror exact");
                chkMirror.setTextColor(Color.parseColor("#FCA5A5"));
                chkMirror.setTextSize(11);
                chkMirror.setChecked(isMirrorExact);
                chkMirror.setEnabled(isSyncEnabled);
                chkMirror.setPadding(12, 0, 0, 0);
                chkMirror.setOnCheckedChangeListener((btn, isChecked) -> prefs.edit().putBoolean(folderId + "_mirror_exact", isChecked).apply());

                actionsRow.addView(btnFolderAction);
                actionsRow.addView(chkMirror);
                card.addView(actionsRow);

                chkSync.setOnCheckedChangeListener((btn, isChecked) -> {
                    prefs.edit().putBoolean(folderId + "_sync_enabled", isChecked).apply();
                    chkSync.setText(isChecked ? "Active" : "Paused");
                    chkSync.setTextColor(isChecked ? Color.parseColor("#4ADE80") : Color.parseColor("#94A3B8"));
                    chkMirror.setEnabled(isChecked);
                    if (isChecked && isConfirmed) {
                        triggerPcStream(getEffectivePcIp());
                    }
                });

                containerFolderPairs.addView(card);
            }

            if (unconfirmedId != null && !isFinishing()) {
                promptFolderDestination(unconfirmedId, unconfirmedName, unconfirmedPath);
            }
        } catch (Exception ignored) {}
    }

    private String convertUriToStoragePath(Uri treeUri) {
        if (treeUri == null) return "";
        String docId = DocumentsContract.getTreeDocumentId(treeUri);
        String[] parts = docId.split(":");
        if (parts.length >= 2) {
            String type = parts[0];
            String relativePath = parts[1];
            if ("primary".equalsIgnoreCase(type)) {
                return "/storage/emulated/0/" + relativePath;
            } else {return "/storage/" + type + "/" + relativePath;}
        } else if (parts.length == 1 && "primary".equalsIgnoreCase(parts[0])) {return "/storage/emulated/0";}
        return treeUri.getPath();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_PICK_FOLDER && resultCode == RESULT_OK && data != null) {
            Uri treeUri = data.getData();
            if (treeUri != null && activePickingFolderId != null) {
                try {
                    getContentResolver().takePersistableUriPermission(treeUri, Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
                } catch (Exception ignored) {}
                String finalPath = convertUriToStoragePath(treeUri);
                prefs.edit()
                    .putString(activePickingFolderId, finalPath)
                    .putBoolean(activePickingFolderId + "_confirmed", true)
                    .putBoolean(activePickingFolderId + "_sync_enabled", true)
                    .apply();
                activePickingFolderId = null;
                readSourcesFromFile();
                triggerPcStream(getEffectivePcIp());
            }
        }
    }

    @Override
    protected void onDestroy() {
        SyncService.activeListener = null;
        unregisterReceiver(statusReceiver);
        super.onDestroy();
    }
}
