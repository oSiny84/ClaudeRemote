package com.clauderemote.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.*
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "settings")

class AppSettings(private val context: Context) {

    // --- Keys ---
    private object Keys {
        val SERVER_HOST = stringPreferencesKey("server_host")
        val SERVER_PORT = stringPreferencesKey("server_port")
        val AUTO_CONNECT = booleanPreferencesKey("auto_connect")
        val NOTIFY_OUTPUT = booleanPreferencesKey("notify_output")
        val NOTIFY_DISCONNECT = booleanPreferencesKey("notify_disconnect")
        val THEME_MODE = stringPreferencesKey("theme_mode") // system, dark, light
    }

    // --- Flows ---

    val serverHost: Flow<String> = context.dataStore.data.map {
        it[Keys.SERVER_HOST] ?: "192.168.0.1"
    }

    val serverPort: Flow<String> = context.dataStore.data.map {
        it[Keys.SERVER_PORT] ?: "8765"
    }

    val autoConnect: Flow<Boolean> = context.dataStore.data.map {
        it[Keys.AUTO_CONNECT] ?: false
    }

    val notifyOutput: Flow<Boolean> = context.dataStore.data.map {
        it[Keys.NOTIFY_OUTPUT] ?: true
    }

    val notifyDisconnect: Flow<Boolean> = context.dataStore.data.map {
        it[Keys.NOTIFY_DISCONNECT] ?: true
    }

    val themeMode: Flow<String> = context.dataStore.data.map {
        it[Keys.THEME_MODE] ?: "system"
    }

    // --- Setters ---

    suspend fun setServerHost(host: String) {
        context.dataStore.edit { it[Keys.SERVER_HOST] = host }
    }

    suspend fun setServerPort(port: String) {
        context.dataStore.edit { it[Keys.SERVER_PORT] = port }
    }

    suspend fun setAutoConnect(enabled: Boolean) {
        context.dataStore.edit { it[Keys.AUTO_CONNECT] = enabled }
    }

    suspend fun setNotifyOutput(enabled: Boolean) {
        context.dataStore.edit { it[Keys.NOTIFY_OUTPUT] = enabled }
    }

    suspend fun setNotifyDisconnect(enabled: Boolean) {
        context.dataStore.edit { it[Keys.NOTIFY_DISCONNECT] = enabled }
    }

    suspend fun setThemeMode(mode: String) {
        context.dataStore.edit { it[Keys.THEME_MODE] = mode }
    }

    /** Save host and port together */
    suspend fun saveConnection(host: String, port: String) {
        context.dataStore.edit {
            it[Keys.SERVER_HOST] = host
            it[Keys.SERVER_PORT] = port
        }
    }
}
