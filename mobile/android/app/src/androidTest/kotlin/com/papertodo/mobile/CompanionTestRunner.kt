package com.papertodo.mobile

import android.app.Activity
import android.app.Instrumentation
import android.app.job.JobScheduler
import android.os.Bundle
import io.flutter.FlutterInjector
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.embedding.engine.dart.DartExecutor
import org.json.JSONObject
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Destructive fixtures are restricted to the dedicated automation application. */
class CompanionTestRunner : Instrumentation() {
    private var arguments = Bundle()
    override fun onCreate(arguments: Bundle?) { super.onCreate(arguments); this.arguments = arguments ?: Bundle(); start() }
    override fun onStart() {
        val result = Bundle()
        val engines = mutableListOf<FlutterEngine>()
        val cleanups = mutableListOf<() -> Unit>()
        var outcome = Activity.RESULT_OK
        var authorizedTestPackage = false
        try {
            val context = targetContext
            check(context.packageName.endsWith(".integration")) { "Fixtures require the dedicated .integration application; user/dev data must not be touched" }
            authorizedTestPackage = true
            val fixturePath = arguments.getString("fixture") ?: error("fixture argument required")
            val state = JSONObject(File(fixturePath).readText())
            val serverUrl = arguments.getString("serverUrl")
            state.put("syncEnabled", serverUrl != null).put("syncProvider", "custom")
                .put("syncCustomServerUrl", serverUrl ?: "").put("syncCustomToken", "audit-only-token")
            val docs = context.getDir("flutter", 0)
            File(docs, "data.json").writeText(state.toString())
            File(docs, "data.json.backup").delete()
            for (name in listOf("widget-actions.json", "widget-actions.json.bak")) File(context.filesDir, name).delete()
            val todo = state.getJSONArray("papers").getJSONObject(1)
            val todoItems = todo.getJSONArray("items")
            val item = (0 until todoItems.length()).map { todoItems.getJSONObject(it) }.first { it.optString("id") == "item" }
            val cache = JSONObject().put("papers", org.json.JSONArray().put(todo)).put("notificationsEnabled", false)
            CompanionCache.update(context, cache.toString())
            CompanionCache.setDone(context, todo.getString("id"), item.getString("id"), true)
            check(CompanionCache.actions(context).length() == 1) { "Widget action not durable" }
            val cachedItems = CompanionCache.state(context).getJSONArray("papers").getJSONObject(0).getJSONArray("items")
            check((0 until cachedItems.length()).map { cachedItems.getJSONObject(it) }.any { it.optString("id") == "item" && it.optBoolean("done") })
            val engineCount = arguments.getString("parallelEngines")?.toInt()?.coerceIn(1, 2) ?: 1
            val done = CountDownLatch(engineCount)
            var retry = false
            runOnMainSync {
                val loader = FlutterInjector.instance().flutterLoader()
                loader.startInitialization(context)
                loader.ensureInitializationComplete(context, null)
                repeat(engineCount) {
                    val worker = FlutterEngine(context)
                    engines.add(worker)
                    cleanups.add(CompanionBridge.attach(context, worker) { needsRetry -> retry = retry || needsRetry; done.countDown() })
                    worker.dartExecutor.executeDartEntrypoint(DartExecutor.DartEntrypoint(loader.findAppBundlePath(), "backgroundSync"))
                }
            }
            check(done.await(90, TimeUnit.SECONDS)) { "Dart background job timed out" }
            check(!retry) { "Background sync reported failure" }
            val persisted = JSONObject(File(docs, "data.json").readText())
            val papers = persisted.getJSONArray("papers")
            var foundDone = false
            var foundCanvas = false
            var foundServerPaper = false
            for (p in 0 until papers.length()) {
                val paper = papers.getJSONObject(p)
                if (paper.optString("id") == "todo") {
                    val items = paper.getJSONArray("items")
                    foundDone = (0 until items.length()).map { items.getJSONObject(it) }.any { it.optString("id") == "item" && it.optBoolean("done") }
                }
                if (paper.optString("id") == "note") foundCanvas = paper.getJSONArray("noteCanvasElements").length() == 1
                if (paper.optString("id") == "server-note") foundServerPaper = true
            }
            check(foundDone) { "Widget change did not reach data.json" }
            check(foundCanvas) { "Code canvas was lost" }
            check(CompanionCache.actions(context).length() == 0) { "Persisted actions were not acknowledged" }
            if (serverUrl != null) {
                check(foundServerPaper) { "Remote paper not received" }
                check(context.getSystemService(JobScheduler::class.java).getPendingJob(27100) != null) { "Periodic sync not registered" }
            }
            result.putString("stream", "PASS: native widget queue, actual Dart background sync, persisted checkbox, canvas preservation, action acknowledgement, periodic job registration.\n")
            result.putString("sharedState", JSONObject().put("schemaVersion", 1).put("papers", papers)
                .put("syncTombstones", persisted.optJSONArray("syncTombstones") ?: org.json.JSONArray()).toString())
        } catch (error: Throwable) {
            val trace = java.io.StringWriter()
            error.printStackTrace(java.io.PrintWriter(trace))
            result.putString("stream", "FAIL: $trace\n")
            outcome = Activity.RESULT_CANCELED
        } finally {
            if (authorizedTestPackage) targetContext.getSystemService(JobScheduler::class.java).cancelAll()
            runOnMainSync { engines.forEach { it.destroy() }; cleanups.forEach { it() } }
        }
        finish(outcome, result)
    }
}
