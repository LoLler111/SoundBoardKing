package com.soundboardking.remote.ui

import android.app.Application
import android.os.Build
import android.os.SystemClock
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.soundboardking.remote.BuildConfig
import com.soundboardking.remote.data.ApkInstaller
import com.soundboardking.remote.data.AppRelease
import com.soundboardking.remote.data.AppUpdater
import com.soundboardking.remote.data.Discovery
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.data.LookStore
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.data.DiscoveredPc
import com.soundboardking.remote.data.MicDto
import com.soundboardking.remote.data.PROTOCOL_VERSION
import com.soundboardking.remote.data.PairingLink
import com.soundboardking.remote.data.PairingRefusedException
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.PlaybackDto
import com.soundboardking.remote.data.SavedServer
import com.soundboardking.remote.data.ServerEvent
import com.soundboardking.remote.data.ServerStore
import com.soundboardking.remote.data.SoundBoardApi
import com.soundboardking.remote.data.StateDto
import com.soundboardking.remote.data.UnauthorizedException
import com.soundboardking.remote.data.parseHostAndPort
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.SerializationException
import okhttp3.WebSocket
import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

enum class ConnectionState { Connecting, Online, Offline }

data class UiState(
    val server: SavedServer? = null,
    val connection: ConnectionState = ConnectionState.Offline,
    val connectionMessage: String? = null,
    val library: StateDto? = null,
    val playback: PlaybackDto = PlaybackDto(),
    /** SystemClock.elapsedRealtime() when [playback] arrived; the position is extrapolated from it. */
    val playbackReceivedAt: Long = 0,
    val mic: MicDto = MicDto(),
    val effect: SoundEffect = SoundEffect.None,
    val effectAmount: Int = 70,
    val discovered: List<DiscoveredPc> = emptyList(),
    val discovering: Boolean = false,
    val pairingBusy: Boolean = false,
    val pairingError: String? = null,
    val look: FavoritesLook = FavoritesLook(),
)

/** The in-app update, kept apart from [UiState] so pairing and reconnecting never reset it. */
sealed interface UpdateState {
    data object Idle : UpdateState
    data object Checking : UpdateState
    data object UpToDate : UpdateState
    data class Available(val release: AppRelease) : UpdateState
    data class Downloading(val release: AppRelease, val progress: Float) : UpdateState
    data class Ready(val release: AppRelease, val apk: java.io.File) : UpdateState
    data class Failed(val message: String) : UpdateState
}

class AppViewModel(application: Application) : AndroidViewModel(application), ConnectActions, RemoteActions, UpdateActions {
    private val store = ServerStore(application)
    private val lookStore = LookStore(application)
    private val api = SoundBoardApi()
    private val updater = AppUpdater(BuildConfig.LATEST_RELEASE_URL)

    private val _state = MutableStateFlow(UiState(server = store.load(), look = lookStore.load()))
    val state: StateFlow<UiState> = _state.asStateFlow()

    private val _update = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val update: StateFlow<UpdateState> = _update.asStateFlow()

    private val _updateDialog = MutableStateFlow(false)
    val updateDialog: StateFlow<Boolean> = _updateDialog.asStateFlow()

    private var checkedForUpdate = false
    private var installAfterPermission = false

    private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 8)
    val messages: SharedFlow<String> = _messages

    private var foreground = false
    private var connectJob: Job? = null
    private var socket: WebSocket? = null
    private var socketGeneration = 0
    private var activeBaseUrl: String? = null

    // ---- Lifecycle -------------------------------------------------------------------------

    fun onForeground() {
        foreground = true
        if (_state.value.server != null) connect() else discover()
        if (!checkedForUpdate) {
            checkedForUpdate = true
            checkForUpdate(manual = false)
        }
        resumeInstallAfterPermission()
    }

    /** Nothing runs in the background: the connection is reopened when the app comes back. */
    fun onBackground() {
        foreground = false
        connectJob?.cancel()
        closeSocket()
        _state.update { it.copy(connection = ConnectionState.Offline) }
    }

    override fun reconnect() {
        if (_state.value.server != null) connect()
    }

    override fun showMessage(message: String) {
        _messages.tryEmit(message)
    }

    override fun onCleared() {
        closeSocket()
    }

    // ---- Pairing ---------------------------------------------------------------------------

    override fun discover() {
        if (_state.value.discovering) return
        viewModelScope.launch {
            _state.update { it.copy(discovering = true) }
            val found = runCatching { Discovery.find() }.getOrDefault(emptyList())
            _state.update { it.copy(discovering = false, discovered = found) }
        }
    }

    override fun pairWithLink(text: String) {
        val link = PairingLink.parse(text)
        if (link == null) {
            _state.update { it.copy(pairingError = "Questo QR non è un codice di abbinamento di SoundBoardKing.") }
            return
        }
        pair(link.hosts, link.port, link.code)
    }

    override fun pairWithDiscovered(pc: DiscoveredPc, code: String) = pair(listOf(pc.host), pc.reply.port, code)

    override fun pairManually(address: String, code: String) {
        val (host, port) = parseHostAndPort(address) ?: run {
            _state.update { it.copy(pairingError = "Indirizzo non valido. Esempio: 192.168.1.10") }
            return
        }
        pair(listOf(host), port, code)
    }

    override fun setLook(look: FavoritesLook) {
        lookStore.save(look)
        _state.update { it.copy(look = look) }
    }

    override fun forgetServer() {
        connectJob?.cancel()
        closeSocket()
        store.clear()
        _state.value = UiState()
        discover()
    }

    private fun pair(hosts: List<String>, port: Int, code: String) {
        val digits = code.filter(Char::isDigit)
        if (digits.length != 6) {
            _state.update { it.copy(pairingError = "Il codice ha 6 cifre.") }
            return
        }
        viewModelScope.launch {
            _state.update { it.copy(pairingBusy = true, pairingError = null, connectionMessage = null) }
            var failure: Throwable? = null
            for (host in hosts) {
                val baseUrl = SoundBoardApi.baseUrl(host, port)
                try {
                    val info = api.info(baseUrl)
                    if (info.protocol != PROTOCOL_VERSION)
                        throw IOException("Versione del PC non compatibile: aggiorna SoundBoardKing su PC e telefono.")
                    val paired = api.pair(baseUrl, digits, deviceName())
                    val server = SavedServer(info.serverId, info.name, listOf(host) + hosts.filter { it != host }, port, paired.token)
                    store.save(server)
                    closeSocket()
                    _state.update { UiState(server = server) }
                    connect()
                    return@launch
                } catch (e: PairingRefusedException) {
                    failure = e
                    break // the code is wrong everywhere, no point trying other addresses
                } catch (e: IOException) {
                    failure = e
                } catch (e: SerializationException) {
                    failure = IOException("Risposta non valida: a quell'indirizzo non c'è SoundBoardKing.")
                }
            }
            _state.update { it.copy(pairingBusy = false, pairingError = describe(failure)) }
        }
    }

    // ---- App update ------------------------------------------------------------------------

    override val appVersion: String get() = BuildConfig.VERSION_NAME

    override fun showUpdate() {
        _updateDialog.value = true
        if (_update.value is UpdateState.Idle || _update.value is UpdateState.Failed || _update.value is UpdateState.UpToDate)
            checkForUpdate(manual = true)
    }

    override fun dismissUpdate() {
        _updateDialog.value = false
    }

    /** Automatic at the first start of the session (quiet when offline); [manual] from the menu. */
    override fun checkForUpdate(manual: Boolean) {
        if (_update.value is UpdateState.Checking || _update.value is UpdateState.Downloading) return
        viewModelScope.launch {
            _update.value = UpdateState.Checking
            _update.value = try {
                when (val release = updater.findUpdate(appVersion)) {
                    null -> UpdateState.UpToDate
                    else -> UpdateState.Available(release).also { _updateDialog.value = true }
                }
            } catch (e: IOException) {
                if (manual) UpdateState.Failed("Controllo non riuscito: ${e.message ?: "nessuna connessione"}") else UpdateState.Idle
            } catch (e: SerializationException) {
                UpdateState.Failed("Risposta di GitHub non valida.")
            }
        }
    }

    override fun downloadUpdate() {
        val release = (_update.value as? UpdateState.Available)?.release ?: return
        viewModelScope.launch {
            _update.value = UpdateState.Downloading(release, 0f)
            _update.value = try {
                val folder = ApkInstaller.updatesFolder(getApplication())
                val apk = updater.download(release, folder) { progress ->
                    _update.value = UpdateState.Downloading(release, progress)
                }
                UpdateState.Ready(release, apk)
            } catch (e: IOException) {
                UpdateState.Failed("Download non riuscito: ${e.message ?: "nessuna connessione"}")
            }
            if (_update.value is UpdateState.Ready) installUpdate()
        }
    }

    override fun installUpdate() {
        val ready = _update.value as? UpdateState.Ready ?: return
        val context = getApplication<Application>()
        if (!ApkInstaller.canInstall(context)) {
            // Allowed once in the system settings; the installer opens when the user comes back.
            installAfterPermission = true
            ApkInstaller.openInstallPermission(context)
            return
        }
        if (!ApkInstaller.install(context, ready.apk))
            _update.value = UpdateState.Failed("Impossibile aprire l'installazione dell'aggiornamento.")
    }

    private fun resumeInstallAfterPermission() {
        if (!installAfterPermission) return
        installAfterPermission = false
        if (ApkInstaller.canInstall(getApplication())) installUpdate()
        else showMessage("Per aggiornare, consenti a SoundBoardKing di installare app.")
    }

    // ---- Connection ------------------------------------------------------------------------

    private sealed interface Outcome {
        data object Connected : Outcome
        data object Unauthorized : Outcome
        data class Failed(val message: String) : Outcome
    }

    private fun connect() {
        connectJob?.cancel()
        connectJob = viewModelScope.launch {
            var backoffMs = 1_000L
            while (isActive && foreground) {
                val server = _state.value.server ?: return@launch
                _state.update { it.copy(connection = ConnectionState.Connecting) }
                when (val outcome = tryConnect(server)) {
                    Outcome.Connected -> return@launch // from here on the socket listener drives reconnection
                    Outcome.Unauthorized -> {
                        store.clear()
                        closeSocket()
                        _state.value = UiState(connectionMessage = "Il PC ha rimosso questo telefono: abbinalo di nuovo.")
                        discover()
                        return@launch
                    }
                    is Outcome.Failed -> _state.update {
                        it.copy(connection = ConnectionState.Offline, connectionMessage = outcome.message)
                    }
                }
                delay(backoffMs)
                backoffMs = (backoffMs * 2).coerceAtMost(15_000)
            }
        }
    }

    private suspend fun tryConnect(server: SavedServer): Outcome {
        var lastError: Throwable? = null
        var hosts = server.hosts
        repeat(2) { attempt ->
            for (host in hosts) {
                val baseUrl = SoundBoardApi.baseUrl(host, server.port)
                try {
                    val library = api.state(baseUrl, server.token)
                    rememberHost(server, host)
                    applyState(library)
                    _state.update { it.copy(connectionMessage = null) }
                    openSocket(baseUrl, server.token)
                    return Outcome.Connected
                } catch (_: UnauthorizedException) {
                    return Outcome.Unauthorized
                } catch (e: IOException) {
                    lastError = e
                } catch (e: SerializationException) {
                    lastError = e
                }
            }
            // The PC may have a new address (DHCP): look for it on the network by its id.
            if (attempt == 0) {
                val found = runCatching { Discovery.find(server.port) }.getOrDefault(emptyList())
                    .firstOrNull { it.reply.serverId == server.serverId }
                    ?: return Outcome.Failed(describe(lastError))
                hosts = listOf(found.host)
            }
        }
        return Outcome.Failed(describe(lastError))
    }

    private fun rememberHost(server: SavedServer, host: String) {
        if (server.hosts.firstOrNull() == host) return
        val updated = server.copy(hosts = (listOf(host) + server.hosts).distinct().take(4))
        store.save(updated)
        _state.update { it.copy(server = updated) }
    }

    private fun applyState(library: StateDto) {
        _state.update {
            it.copy(
                library = library,
                playback = library.playback,
                playbackReceivedAt = SystemClock.elapsedRealtime(),
                mic = library.mic,
                effect = SoundEffect.fromWire(library.effect),
                effectAmount = library.effectAmount,
            )
        }
    }

    private fun openSocket(baseUrl: String, token: String) {
        closeSocket()
        activeBaseUrl = baseUrl
        val generation = ++socketGeneration
        socket = api.connect(baseUrl, token, object : SoundBoardApi.Listener {
            override fun onOpen() {
                if (generation == socketGeneration)
                    _state.update { it.copy(connection = ConnectionState.Online) }
            }

            override fun onEvent(event: ServerEvent) {
                viewModelScope.launch { if (generation == socketGeneration) handle(event) }
            }

            override fun onClosed(error: Throwable?) {
                viewModelScope.launch {
                    if (generation != socketGeneration) return@launch
                    socket = null
                    _state.update { it.copy(connection = ConnectionState.Offline) }
                    if (foreground) connect()
                }
            }
        })
    }

    private fun closeSocket() {
        socketGeneration++
        socket?.close(1000, null)
        socket = null
    }

    private fun handle(event: ServerEvent) {
        when (event) {
            is ServerEvent.Hello ->
                if (event.libraryVersion != _state.value.library?.libraryVersion) refreshLibrary()
            is ServerEvent.LibraryChanged -> refreshLibrary()
            is ServerEvent.Playback -> _state.update {
                it.copy(playback = event.playback, playbackReceivedAt = SystemClock.elapsedRealtime())
            }
            is ServerEvent.Mic -> _state.update { it.copy(mic = event.mic) }
            is ServerEvent.Effect -> _state.update { it.copy(effect = SoundEffect.fromWire(event.effect), effectAmount = event.amount) }
            is ServerEvent.Error -> showMessage(event.message)
        }
    }

    private fun refreshLibrary() {
        val baseUrl = activeBaseUrl ?: return
        val token = _state.value.server?.token ?: return
        viewModelScope.launch {
            try {
                applyState(api.state(baseUrl, token))
            } catch (_: IOException) {
                // The socket listener notices a dead connection and reconnects.
            } catch (_: SerializationException) {
            }
        }
    }

    // ---- Commands --------------------------------------------------------------------------

    override fun play(soundId: String, target: PlayTarget) = send(SoundBoardApi.playCommand(soundId, target))

    override fun stop() = send(SoundBoardApi.simpleCommand("stop"))

    override fun stopOne(playbackId: String) = send(SoundBoardApi.stopOneCommand(playbackId))

    override fun togglePauseOne(playbackId: String) = send(SoundBoardApi.togglePauseOneCommand(playbackId))

    override fun togglePause() = send(SoundBoardApi.simpleCommand("togglePause"))

    override fun setMic(enabled: Boolean) = send(SoundBoardApi.micCommand(enabled))

    override fun setEffect(effect: SoundEffect) = send(SoundBoardApi.effectCommand(effect))

    // Shown at once so the knob follows the finger; the PC confirms with its own event.
    override fun setEffectAmount(percent: Int) {
        _state.update { it.copy(effectAmount = percent) }
        send(SoundBoardApi.effectAmountCommand(percent))
    }

    private fun send(command: String) {
        if (socket?.send(command) != true) showMessage("Non connesso al PC.")
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private fun deviceName(): String {
        val model = Build.MODEL.orEmpty()
        val manufacturer = Build.MANUFACTURER.orEmpty().replaceFirstChar { it.uppercase() }
        return if (model.startsWith(manufacturer, ignoreCase = true)) model else "$manufacturer $model".trim()
    }

    private fun describe(error: Throwable?): String = when (error) {
        null -> "PC non trovato."
        is ConnectException, is SocketTimeoutException, is UnknownHostException ->
            "PC non raggiungibile. Il telefono è sulla stessa rete Wi-Fi? SoundBoardKing è aperto e consentito nel firewall?"
        is SerializationException -> "Risposta non valida dal PC."
        else -> error.message ?: "Errore di connessione."
    }
}
