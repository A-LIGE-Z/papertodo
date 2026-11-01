package com.papertodo.mobile

import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import java.text.SimpleDateFormat
import java.util.Locale

object CompanionNotifications {
    private const val CHANNEL = "papertodo_due_reminders"
    fun permitted(context: Context): Boolean = NotificationManagerCompat.from(context).areNotificationsEnabled() &&
        (Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)

    fun show(context: Context, id: Int, title: String, body: String): Boolean {
        if (!permitted(context)) return false
        val manager = context.getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.reminder_channel), NotificationManager.IMPORTANCE_HIGH))
            if (manager.getNotificationChannel(CHANNEL)?.importance == NotificationManager.IMPORTANCE_NONE) return false
        }
        val pending = PendingIntent.getActivity(context, 0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        manager.notify(id, NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title).setContentText(body).setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setContentIntent(pending).setAutoCancel(true).setPriority(NotificationCompat.PRIORITY_HIGH).build())
        return true
    }

    @Synchronized fun refresh(context: Context) {
        val state = CompanionCache.state(context)
        val alarm = context.getSystemService(AlarmManager::class.java)
        val intent = PendingIntent.getBroadcast(context, 27000, Intent(context, CompanionAlarmReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        alarm.cancel(intent)
        val preferences = context.getSharedPreferences("companion-notifications", Context.MODE_PRIVATE)
        val delivered = preferences.getStringSet("delivered", emptySet())!!.toMutableSet()
        val active = preferences.getStringSet("active", emptySet())!!.toMutableSet()
        val stillActive = mutableSetOf<String>()
        val validKeys = mutableSetOf<String>()
        val enabled = state.optBoolean("notificationsEnabled", true)
        val now = System.currentTimeMillis()
        val lead = state.optInt("notificationLeadMinutes", 15).coerceIn(0, 1440) * 60_000L
        var next = Long.MAX_VALUE
        val parser = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.US).apply { isLenient = false }
        val papers = state.optJSONArray("papers")
        if (papers != null) for (p in 0 until papers.length()) {
            val paper = papers.getJSONObject(p)
            val items = paper.optJSONArray("items") ?: continue
            for (i in 0 until items.length()) {
                val item = items.getJSONObject(i)
                if (item.optBoolean("done")) continue
                val raw = item.optString("dueAtLocal")
                val due = try { parser.parse(raw.take(19))?.time } catch (_: Exception) { null } ?: continue
                val id = item.getString("id")
                val upcoming = "$id|$raw|upcoming"
                val overdue = "$id|$raw|overdue"
                validKeys.add(upcoming); validKeys.add(overdue)
                if (!enabled) continue
                stillActive.add(id)
                val phase = if (due <= now) overdue else upcoming
                val shouldNotify = if (due <= now) state.optBoolean("notifyOverdue", true) else now >= due - lead
                if (shouldNotify && !delivered.contains(phase)) {
                    val title = context.getString(if (due <= now) R.string.reminder_overdue else R.string.reminder_upcoming)
                    if (show(context, id.hashCode() and 0x7fffffff, title, "${paper.optString("title")}\n${item.optString("text")}")) delivered.add(phase)
                }
                if (due > now) {
                    if (due - lead > now && !delivered.contains(upcoming)) next = minOf(next, due - lead)
                    if (state.optBoolean("notifyOverdue", true)) next = minOf(next, due)
                }
            }
        }
        val manager = context.getSystemService(NotificationManager::class.java)
        for (id in active - stillActive) manager.cancel(id.hashCode() and 0x7fffffff)
        delivered.retainAll(validKeys)
        preferences.edit().putStringSet("delivered", delivered).putStringSet("active", stillActive).apply()
        if (enabled && permitted(context) && next != Long.MAX_VALUE) {
            // Works without exact-alarm permission. Android may delay it in power-saving modes.
            alarm.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, maxOf(now + 1000, next), intent)
        }
    }
}

class CompanionAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        CompanionNotifications.refresh(context)
        CompanionSyncJob.schedulePeriodic(context)
        PaperTodoWidget.refresh(context)
    }
}
