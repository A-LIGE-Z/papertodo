package com.papertodo.mobile

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Paint
import android.net.Uri
import android.widget.RemoteViews
import android.widget.RemoteViewsService

class PaperTodoWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        for (id in ids) {
            val views = RemoteViews(context.packageName, R.layout.paper_todo_widget)
            val adapter = Intent(context, PaperTodoWidgetService::class.java).putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id)
                .setData(Uri.parse("papertodo://widget/$id"))
            views.setRemoteAdapter(R.id.widget_list, adapter)
            views.setEmptyView(R.id.widget_list, R.id.widget_empty)
            val open = PendingIntent.getActivity(context, id, Intent(context, MainActivity::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            views.setOnClickPendingIntent(R.id.widget_title, open)
            views.setOnClickPendingIntent(R.id.widget_empty, open)
            val refresh = PendingIntent.getBroadcast(context, id,
                Intent(context, PaperTodoWidget::class.java).setAction(REFRESH),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            views.setOnClickPendingIntent(R.id.widget_refresh, refresh)
            val toggle = PendingIntent.getBroadcast(context, id, Intent(context, PaperTodoWidget::class.java).setAction(TOGGLE),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
            views.setPendingIntentTemplate(R.id.widget_list, toggle)
            manager.updateAppWidget(id, views)
        }
    }
    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == TOGGLE) {
            val paper = intent.getStringExtra("paperId") ?: return
            val item = intent.getStringExtra("itemId") ?: return
            try {
                CompanionCache.setDone(context, paper, item, intent.getBooleanExtra("done", true))
                refresh(context)
                CompanionNotifications.refresh(context)
                CompanionBridge.notifyActions()
                CompanionSyncJob.scheduleNow(context)
            } catch (error: Exception) {
                android.util.Log.e("PaperTodoWidget", "Widget action was not committed", error)
            }
        } else if (intent.action == REFRESH) {
            CompanionBridge.notifyActions()
            CompanionSyncJob.scheduleNow(context)
        }
    }
    companion object {
        private const val TOGGLE = "com.papertodo.mobile.WIDGET_SET_DONE"
        private const val REFRESH = "com.papertodo.mobile.WIDGET_REFRESH"
        fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, PaperTodoWidget::class.java))
            manager.notifyAppWidgetViewDataChanged(ids, R.id.widget_list)
        }
    }
}

class PaperTodoWidgetService : RemoteViewsService() {
    override fun onGetViewFactory(intent: Intent): RemoteViewsFactory = Factory(applicationContext)
    private class Factory(val context: Context) : RemoteViewsFactory {
        data class Row(val paperId: String, val paperTitle: String, val id: String, val text: String, val done: Boolean)
        private var rows = listOf<Row>()
        override fun onCreate() { onDataSetChanged() }
        override fun onDataSetChanged() {
            val next = mutableListOf<Row>()
            val papers = CompanionCache.state(context).optJSONArray("papers")
            if (papers != null) for (p in 0 until papers.length()) {
                val paper = papers.getJSONObject(p)
                val items = paper.optJSONArray("items") ?: continue
                for (i in 0 until items.length()) {
                    val item = items.getJSONObject(i)
                    next.add(Row(paper.getString("id"), paper.optString("title"), item.getString("id"),
                        item.optString("text"), item.optBoolean("done")))
                }
            }
            rows = next.sortedBy { it.done }
        }
        override fun getViewAt(position: Int): RemoteViews? {
            val row = rows.getOrNull(position) ?: return null
            return RemoteViews(context.packageName, R.layout.paper_todo_widget_row).apply {
                setTextViewText(R.id.widget_row_title, row.paperTitle)
                setTextViewText(R.id.widget_row_text, row.text)
                setTextViewText(R.id.widget_row_check, if (row.done) "✓" else "○")
                setInt(R.id.widget_row_text, "setPaintFlags", Paint.ANTI_ALIAS_FLAG or if (row.done) Paint.STRIKE_THRU_TEXT_FLAG else 0)
                setContentDescription(R.id.widget_row_check, context.getString(if (row.done) R.string.widget_undo else R.string.widget_complete))
                setOnClickFillInIntent(R.id.widget_row, Intent().putExtra("paperId", row.paperId).putExtra("itemId", row.id).putExtra("done", !row.done))
            }
        }
        override fun getCount() = rows.size
        override fun getItemId(position: Int) = rows.getOrNull(position)?.id?.hashCode()?.toLong() ?: position.toLong()
        override fun getLoadingView(): RemoteViews? = null
        override fun getViewTypeCount() = 1
        override fun hasStableIds() = true
        override fun onDestroy() { rows = emptyList() }
    }
}
