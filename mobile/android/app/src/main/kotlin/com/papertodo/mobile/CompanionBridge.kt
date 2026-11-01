package com.papertodo.mobile

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.AtomicFile
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.UUID

object CompanionStorageGate {
    private data class Waiter(val engine: String, val lease: String, val result: MethodChannel.Result)
    private var owner: Waiter? = null
    private val waiting = ArrayDeque<Waiter>()
    @Synchronized fun acquire(engine: String, lease: String, result: MethodChannel.Result) {
        val waiter = Waiter(engine, lease, result)
        if (owner == null) { owner = waiter; result.success(null) } else waiting.addLast(waiter)
    }
    @Synchronized fun release(engine: String, lease: String) {
        if (owner?.engine == engine && owner?.lease == lease) { owner = null; next() }
    }
    @Synchronized fun detach(engine: String) {
        waiting.removeAll { it.engine == engine }
        if (owner?.engine == engine) { owner = null; next() }
    }
    private fun next() {
        if (waiting.isNotEmpty()) { owner = waiting.removeFirst(); owner!!.result.success(null) }
    }
}

object CompanionCache {
    private fun cache(context: Context) = AtomicFile(File(context.filesDir, "companion.json"))
    private fun queue(context: Context) = AtomicFile(File(context.filesDir, "widget-actions.json"))
    private fun read(file: AtomicFile): String? = if (file.baseFile.exists() || File(file.baseFile.path + ".bak").exists())
        String(file.readFully(), Charsets.UTF_8) else null
    private fun write(file: AtomicFile, text: String) {
        val output = file.startWrite()
        try { output.write(text.toByteArray(Charsets.UTF_8)); file.finishWrite(output) }
        catch (error: Exception) { file.failWrite(output); throw error }
    }
    @Synchronized fun state(context: Context): JSONObject = try { JSONObject(read(cache(context)) ?: "{}") }
        catch (_: Exception) { JSONObject() } // Display cache only; authoritative data.json is untouched.
    @Synchronized fun actions(context: Context): JSONArray = JSONArray(read(queue(context)) ?: "[]")

    private fun applyAction(state: JSONObject, action: JSONObject) {
        val papers = state.optJSONArray("papers") ?: return
        for (p in 0 until papers.length()) {
            val paper = papers.getJSONObject(p)
            if (paper.optString("id") != action.optString("paperId")) continue
            val items = paper.optJSONArray("items") ?: continue
            for (i in 0 until items.length()) {
                val item = items.getJSONObject(i)
                if (item.optString("id") == action.optString("itemId") &&
                    item.optLong("updatedAt") <= action.optLong("updatedAt")) {
                    item.put("done", action.getBoolean("done")); item.put("updatedAt", action.getLong("updatedAt"))
                }
            }
        }
    }
    @Synchronized fun update(context: Context, text: String) {
        val state = JSONObject(text)
        val pending = actions(context)
        for (i in 0 until pending.length()) applyAction(state, pending.getJSONObject(i))
        write(cache(context), state.toString())
    }
    @Synchronized fun acknowledge(context: Context, ids: Set<String>) {
        val pending = actions(context)
        val remaining = JSONArray()
        for (i in 0 until pending.length()) {
            val action = pending.getJSONObject(i)
            if (!ids.contains(action.optString("id"))) remaining.put(action)
        }
        write(queue(context), remaining.toString())
    }
    @Synchronized fun setDone(context: Context, paperId: String, itemId: String, done: Boolean) {
        val state = state(context)
        val papers = state.optJSONArray("papers") ?: return
        var previous: JSONObject? = null
        for (p in 0 until papers.length()) {
            val paper = papers.getJSONObject(p)
            if (paper.optString("id") != paperId) continue
            val items = paper.optJSONArray("items") ?: continue
            for (i in 0 until items.length()) if (items.getJSONObject(i).optString("id") == itemId) previous = items.getJSONObject(i)
        }
        val item = previous ?: return
        val action = JSONObject().put("id", UUID.randomUUID().toString()).put("paperId", paperId)
            .put("itemId", itemId).put("done", done).put("updatedAt", maxOf(System.currentTimeMillis(), item.optLong("updatedAt") + 1))
        val pending = actions(context)
        pending.put(action)
        write(queue(context), pending.toString()) // Durable action before visual feedback.
        applyAction(state, action)
        write(cache(context), state.toString())
    }
}

object CompanionBridge {
    var foreground = false
    private val uiChannels = mutableMapOf<String, MethodChannel>()
    fun notifyActions() { for (channel in uiChannels.values) channel.invokeMethod("actionsChanged", null) }
    fun attach(context: Context, engine: FlutterEngine, ui: Boolean = false, completed: ((Boolean) -> Unit)? = null): () -> Unit {
        val owner = UUID.randomUUID().toString()
        val channel = MethodChannel(engine.dartExecutor.binaryMessenger, "com.papertodo.mobile/companion")
        if (ui) uiChannels[owner] = channel
        channel.setMethodCallHandler { call, result ->
            try {
                when (call.method) {
                    "storageAcquire" -> CompanionStorageGate.acquire(owner, call.arguments as String, result)
                    "storageRelease" -> { CompanionStorageGate.release(owner, call.arguments as String); result.success(null) }
                    "pendingActions" -> result.success(CompanionCache.actions(context).toString())
                    "ackActions" -> { CompanionCache.acknowledge(context, (call.arguments as List<*>).filterIsInstance<String>().toSet()); result.success(null) }
                    "update" -> {
                        CompanionCache.update(context, call.arguments as String)
                        PaperTodoWidget.refresh(context)
                        CompanionNotifications.refresh(context)
                        CompanionSyncJob.schedulePeriodic(context)
                        result.success(null)
                    }
                    "backgroundComplete" -> {
                        result.success(null)
                        Handler(Looper.getMainLooper()).post { completed?.invoke(call.arguments == true) }
                    }
                    else -> result.notImplemented()
                }
            } catch (error: Exception) { result.error("companion_storage", error.message, null) }
        }
        return { uiChannels.remove(owner); channel.setMethodCallHandler(null); CompanionStorageGate.detach(owner) }
    }
}
