package com.papertodo.mobile

import android.os.Build
import android.app.Activity
import android.content.Intent
import java.io.ByteArrayOutputStream
import androidx.core.app.ActivityCompat
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    private var cleanupCompanion: (() -> Unit)? = null
    private val permissionResults = mutableListOf<MethodChannel.Result>()
    private var transferResult: MethodChannel.Result? = null
    private var exportText: String? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        cleanupCompanion = CompanionBridge.attach(applicationContext, flutterEngine, ui = true)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "com.papertodo.mobile/backup").setMethodCallHandler { call, result ->
            if (transferResult != null) { result.error("busy", "A file transfer is already open", null); return@setMethodCallHandler }
            when (call.method) {
                "export" -> {
                    transferResult = result
                    exportText = call.arguments as String
                    startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                        .setType("application/json").putExtra(Intent.EXTRA_TITLE, "PaperTodo-backup.json"), 501)
                }
                "import" -> {
                    transferResult = result
                    startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json"), 502)
                }
                else -> result.notImplemented()
            }
        }
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "com.papertodo.mobile/notifications")
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "hasPermission" -> result.success(CompanionNotifications.permitted(this))
                    "requestPermission" -> {
                        if (CompanionNotifications.permitted(this) || Build.VERSION.SDK_INT < 33) {
                            result.success(CompanionNotifications.permitted(this))
                        } else {
                            permissionResults.add(result)
                            if (permissionResults.size == 1) ActivityCompat.requestPermissions(this,
                                arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 101)
                        }
                    }
                    "showNotification" -> result.success(CompanionNotifications.show(this,
                        call.argument<Int>("id") ?: 9999,
                        call.argument<String>("title") ?: "PaperTodo",
                        call.argument<String>("body") ?: ""))
                    else -> result.notImplemented()
                }
            }
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 101) {
            val granted = CompanionNotifications.permitted(this)
            for (result in permissionResults) result.success(granted)
            permissionResults.clear()
            CompanionNotifications.refresh(this)
        }
    }
    override fun onStart() { super.onStart(); CompanionBridge.foreground = true }
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != 501 && requestCode != 502) return
        val result = transferResult ?: return
        transferResult = null
        val uri = data?.data
        if (resultCode != Activity.RESULT_OK || uri == null) { exportText = null; result.success(if (requestCode == 501) false else null); return }
        val text = exportText
        exportText = null
        Thread {
            try {
                val response: Any? = if (requestCode == 501) {
                    contentResolver.openOutputStream(uri, "wt")!!.use { it.write(text!!.toByteArray(Charsets.UTF_8)) }
                    true
                } else {
                    contentResolver.openInputStream(uri)!!.use { stream ->
                        val output = ByteArrayOutputStream()
                        val buffer = ByteArray(8192)
                        while (true) {
                            val count = stream.read(buffer)
                            if (count < 0) break
                            require(output.size() + count <= 32 * 1024 * 1024) { "Backup exceeds 32 MB" }
                            output.write(buffer, 0, count)
                        }
                        output.toString("UTF-8")
                    }
                }
                runOnUiThread { result.success(response) }
            } catch (error: Exception) { runOnUiThread { result.error("transfer", error.message, null) } }
        }.start()
    }
    override fun onStop() { CompanionBridge.foreground = false; super.onStop() }
    override fun onDestroy() {
        super.onDestroy()
        cleanupCompanion?.invoke(); cleanupCompanion = null
    }
}
