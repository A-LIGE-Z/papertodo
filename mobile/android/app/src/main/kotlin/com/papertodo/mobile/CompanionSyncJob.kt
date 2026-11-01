package com.papertodo.mobile

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.Handler
import android.os.Looper
import io.flutter.FlutterInjector
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.embedding.engine.dart.DartExecutor

class CompanionSyncJob : JobService() {
    private var engine: FlutterEngine? = null
    private var cleanup: (() -> Unit)? = null
    private var parameters: JobParameters? = null
    private val handler = Handler(Looper.getMainLooper())
    private val timeout = Runnable { finish(true) }

    override fun onStartJob(params: JobParameters): Boolean {
        if (CompanionBridge.foreground) return false
        if (engine != null) return false
        parameters = params
        val loader = FlutterInjector.instance().flutterLoader()
        loader.startInitialization(applicationContext)
        loader.ensureInitializationComplete(applicationContext, null)
        val backgroundEngine = FlutterEngine(applicationContext)
        engine = backgroundEngine
        cleanup = CompanionBridge.attach(applicationContext, backgroundEngine) { retry -> finish(retry) }
        handler.postDelayed(timeout, 120_000)
        backgroundEngine.dartExecutor.executeDartEntrypoint(DartExecutor.DartEntrypoint(loader.findAppBundlePath(), "backgroundSync"))
        return true
    }
    private fun finish(retry: Boolean) {
        val params = parameters
        parameters = null
        handler.removeCallbacks(timeout)
        engine?.destroy(); engine = null
        cleanup?.invoke(); cleanup = null
        if (params != null) jobFinished(params, retry)
    }
    override fun onStopJob(params: JobParameters): Boolean {
        parameters = null
        finish(false)
        return true
    }
    companion object {
        private const val PERIODIC = 27100
        private const val IMMEDIATE = 27101
        fun schedulePeriodic(context: Context) {
            val scheduler = context.getSystemService(JobScheduler::class.java)
            if (!CompanionCache.state(context).optBoolean("syncEnabled")) { scheduler.cancel(PERIODIC); return }
            if (scheduler.getPendingJob(PERIODIC) != null) return
            scheduler.schedule(JobInfo.Builder(PERIODIC, ComponentName(context, CompanionSyncJob::class.java))
                .setPeriodic(15 * 60 * 1000L).setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setPersisted(true).build())
        }
        fun scheduleNow(context: Context) {
            context.getSystemService(JobScheduler::class.java).schedule(
                JobInfo.Builder(IMMEDIATE, ComponentName(context, CompanionSyncJob::class.java))
                    .setOverrideDeadline(0).build()) // Local widget actions also commit while offline.
        }
    }
}
