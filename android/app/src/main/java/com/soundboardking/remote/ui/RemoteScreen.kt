package com.soundboardking.remote.ui

import com.soundboardking.remote.BuildConfig
import android.os.SystemClock
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.soundboardking.remote.data.CategoryDto
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.data.MicDto
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.PlaybackDto
import com.soundboardking.remote.data.SoundEffect
import com.soundboardking.remote.data.EffectTarget
import com.soundboardking.remote.data.SoundDto
import com.soundboardking.remote.data.StateDto
import com.soundboardking.remote.ui.theme.Danger
import com.soundboardking.remote.ui.theme.Gold
import com.soundboardking.remote.ui.theme.GoldSoft
import com.soundboardking.remote.ui.theme.OnGold
import com.soundboardking.remote.ui.theme.Online
import com.soundboardking.remote.ui.theme.Panel
import com.soundboardking.remote.ui.theme.Panel2
import com.soundboardking.remote.ui.theme.TextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal enum class Tab(val label: String) { Favorites("Preferiti"), Categories("Categorie"), Search("Cerca") }

@Composable
fun RemoteScreen(state: UiState, actions: RemoteActions, snackbar: SnackbarHostState, onUpdates: () -> Unit) =
    RemoteScreen(state, actions, snackbar, Tab.Favorites, onUpdates = onUpdates)

@Composable
internal fun RemoteScreen(
    state: UiState,
    actions: RemoteActions,
    snackbar: SnackbarHostState,
    startTab: Tab,
    startFullScreen: Boolean = false,
    onUpdates: () -> Unit = {},
) {
    KeepScreenOn()
    var tab by rememberSaveable { mutableStateOf(startTab) }
    // Full screen: only the favourites and the playing bars, no app or system bars.
    var fullScreen by rememberSaveable { mutableStateOf(startFullScreen) }
    ImmersiveMode(fullScreen)
    BackHandler(enabled = fullScreen) { fullScreen = false }
    val library = state.library
    val soundsById = remember(library) { library?.sounds?.associateBy { it.id }.orEmpty() }
    val playingIds = state.playback.sounds.mapNotNull { it.soundId }.toSet()
    var lookDialog by rememberSaveable { mutableStateOf(false) }
    if (lookDialog) LookDialog(state.look, onChange = actions::setLook, onDismiss = { lookDialog = false })

    Scaffold(
        topBar = {
            if (!fullScreen) {
                RemoteTopBar(
                    state,
                    onRefresh = actions::reconnect,
                    onForget = actions::forgetServer,
                    onLook = { lookDialog = true },
                    onUpdates = onUpdates,
                    onFullScreen = {
                        tab = Tab.Favorites
                        fullScreen = true
                    },
                )
            }
        },
        bottomBar = {
            Column {
                NowPlayingBar(state, soundsById, actions, showMic = !fullScreen)
                if (!fullScreen) NavigationBar(containerColor = Panel) {
                    Tab.entries.forEach { item ->
                        NavigationBarItem(
                            selected = tab == item,
                            onClick = { tab = item },
                            icon = {
                                Icon(
                                    when (item) {
                                        Tab.Favorites -> Icons.Filled.Star
                                        Tab.Categories -> Icons.AutoMirrored.Filled.List
                                        Tab.Search -> Icons.Filled.Search
                                    },
                                    contentDescription = null,
                                )
                            },
                            label = { Text(item.label) },
                        )
                    }
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        Box(Modifier.padding(padding).fillMaxSize()) {
            if (library == null) {
                Column(
                    Modifier.fillMaxSize().padding(24.dp),
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    if (state.connection == ConnectionState.Connecting) CircularProgressIndicator()
                    Spacer(Modifier.height(16.dp))
                    Text(state.connectionMessage ?: "Connessione al PC…", color = TextDim, textAlign = TextAlign.Center)
                }
            } else {
                when (if (fullScreen) Tab.Favorites else tab) {
                    Tab.Favorites -> FavoritesTab(library, soundsById, playingIds, state.look, actions::play, onExitFullScreen = if (fullScreen) ({ fullScreen = false }) else null)
                    Tab.Categories -> CategoriesTab(library, soundsById, playingIds, actions::play)
                    Tab.Search -> SearchTab(library, playingIds, actions::play)
                }
            }
            if (fullScreen && library == null) {
                IconButton(
                    onClick = { fullScreen = false },
                    modifier = Modifier.align(Alignment.TopEnd).padding(2.dp).size(32.dp).alpha(0.7f),
                ) {
                    FullScreenIcon(exit = true, modifier = Modifier.alpha(0.8f))
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RemoteTopBar(
    state: UiState,
    onRefresh: () -> Unit,
    onForget: () -> Unit,
    onLook: () -> Unit,
    onUpdates: () -> Unit,
    onFullScreen: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    val (statusText, statusColor) = when (state.connection) {
        ConnectionState.Online -> "Connesso" to Online
        ConnectionState.Connecting -> "Connessione…" to Gold
        ConnectionState.Offline -> "Non connesso" to Danger
    }
    TopAppBar(
        colors = TopAppBarDefaults.topAppBarColors(containerColor = Panel),
        title = {
            Column {
                Text(state.server?.name.orEmpty(), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(8.dp).background(statusColor, CircleShape))
                    Spacer(Modifier.width(6.dp))
                    Text(statusText, fontSize = 12.sp, color = TextDim)
                }
            }
        },
        actions = {
            IconButton(onClick = onFullScreen) { FullScreenIcon(exit = false) }
            IconButton(onClick = onRefresh) { Icon(Icons.Filled.Refresh, contentDescription = "Riconnetti") }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "Altro") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(
                        text = { Text("Aspetto dei preferiti") },
                        onClick = {
                            menu = false
                            onLook()
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("Abbina un altro PC") },
                        onClick = {
                            menu = false
                            onForget()
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("Versione ${BuildConfig.VERSION_NAME} · aggiornamenti") },
                        onClick = {
                            menu = false
                            onUpdates()
                        },
                    )
                }
            }
        },
    )
}

// ---- Favourites ------------------------------------------------------------------------------

@Composable
private fun FavoritesTab(
    library: StateDto,
    soundsById: Map<String, SoundDto>,
    playingIds: Set<String>,
    look: FavoritesLook,
    onPlay: OnPlay,
    onExitFullScreen: (() -> Unit)? = null,
) {
    val hotbar = library.hotbar
    val pagerState = rememberPagerState { hotbar.pages }
    val scope = rememberCoroutineScope()
    val slots = remember(library) { hotbar.slots.associateBy { it.page to it.slot } }

    Column(Modifier.fillMaxSize()) {
        if (onExitFullScreen == null) {
            Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                repeat(hotbar.pages) { page ->
                    FilterChip(
                        selected = pagerState.currentPage == page,
                        onClick = { scope.launch { pagerState.animateScrollToPage(page) } },
                        label = { Text("${page + 1}") },
                        colors = FilterChipDefaults.filterChipColors(selectedContainerColor = Gold, selectedLabelColor = OnGold),
                    )
                }
            }
        } else {
            // Full screen: a slim strip with the pages and the way out, so the grid gets nearly all the room.
            Row(Modifier.fillMaxWidth().height(30.dp).padding(horizontal = look.gridMargin), verticalAlignment = Alignment.CenterVertically) {
                repeat(hotbar.pages) { page ->
                    val selected = pagerState.currentPage == page
                    Box(
                        Modifier
                            .padding(end = 4.dp)
                            .size(width = 34.dp, height = 22.dp)
                            .background(if (selected) Gold else Panel2, RoundedCornerShape(6.dp))
                            .clickable { scope.launch { pagerState.animateScrollToPage(page) } },
                        contentAlignment = Alignment.Center,
                    ) { Text("${page + 1}", fontSize = 12.sp, color = if (selected) OnGold else TextDim) }
                }
                Spacer(Modifier.weight(1f))
                IconButton(onClick = onExitFullScreen, modifier = Modifier.size(30.dp)) {
                    FullScreenIcon(exit = true, modifier = Modifier.size(20.dp))
                }
            }
            Spacer(Modifier.height(4.dp))
        }
        HorizontalPager(state = pagerState, modifier = Modifier.weight(1f)) { page ->
            LazyVerticalGrid(
                columns = GridCells.Fixed(look.columns),
                modifier = Modifier.fillMaxSize().padding(horizontal = look.gridMargin),
                horizontalArrangement = Arrangement.spacedBy(look.tileGap),
                verticalArrangement = Arrangement.spacedBy(look.tileGap),
            ) {
                // Up to 100 slots a page: stop after the last row that has a sound, or the page is mostly empty tiles.
                val lastUsed = slots.keys.filter { it.first == page }.maxOfOrNull { it.second } ?: -1
                val rows = (maxOf(lastUsed + 1, MinVisibleSlots) + look.columns - 1) / look.columns
                items(minOf(rows * look.columns, hotbar.slotsPerPage)) { slot ->
                    val sound = slots[page to slot]?.let { soundsById[it.soundId] }
                    SlotTile(slot + 1, sound, playing = sound != null && sound.id in playingIds, look, onPlay)
                }
            }
        }
    }
}

// Even an empty page shows a few tiles, so it doesn't look broken.
private const val MinVisibleSlots = 20

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun SlotTile(number: Int, sound: SoundDto?, playing: Boolean, look: FavoritesLook, onPlay: OnPlay) {
    val haptic = LocalHapticFeedback.current
    var menu by remember { mutableStateOf(false) }
    Box {
        Surface(
            shape = RoundedCornerShape(look.tileCorner),
            color = if (playing) GoldSoft else Panel2,
            border = if (playing) BorderStroke(1.dp, Gold) else null,
            modifier = Modifier
                .aspectRatio(look.aspect)
                .alpha(if (sound == null) 0.35f else if (sound.missing) 0.55f else 1f)
                .combinedClickable(
                    enabled = sound != null,
                    onLongClick = {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        menu = true
                    },
                ) {
                    haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                    sound?.let { onPlay(it.id, PlayTarget.Default) }
                },
        ) {
            TileContent(number, sound?.title.orEmpty(), look)
        }
        if (sound != null) PlayMenu(menu, onDismiss = { menu = false }) { onPlay(sound.id, it) }
    }
}

// ---- Categories ------------------------------------------------------------------------------

@Composable
private fun CategoriesTab(library: StateDto, soundsById: Map<String, SoundDto>, playingIds: Set<String>, onPlay: OnPlay) {
    var path by rememberSaveable { mutableStateOf(listOf<String>()) }
    val trail = remember(library, path) { resolvePath(library.categories, path) }
    val current = trail.lastOrNull()

    // A category may vanish after the library changes on the PC: stop at the deepest one left.
    LaunchedEffect(trail, path) {
        if (trail.size != path.size) path = trail.map { it.id }
    }
    BackHandler(enabled = path.isNotEmpty()) { path = path.dropLast(1) }

    val children = current?.children ?: library.categories
    val sounds = current?.soundIds?.mapNotNull(soundsById::get).orEmpty()

    LazyColumn(Modifier.fillMaxSize()) {
        current?.let { category ->
            item {
                ListItem(
                    headlineContent = { Text(category.name, fontWeight = FontWeight.SemiBold) },
                    leadingContent = { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Indietro") },
                    colors = ListItemDefaults.colors(containerColor = Panel),
                    modifier = Modifier.clickable { path = path.dropLast(1) },
                )
            }
        }
        items(children, key = { "c" + it.id }) { category ->
            ListItem(
                headlineContent = { Text(category.name) },
                supportingContent = { Text("${category.soundIds.size} suoni", color = TextDim) },
                trailingContent = { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null) },
                colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                modifier = Modifier.clickable { path = path + category.id },
            )
        }
        if (children.isNotEmpty() && sounds.isNotEmpty()) item { HorizontalDivider() }
        items(sounds, key = { "s" + it.id }) { sound -> SoundRow(sound, sound.id in playingIds, onPlay) }
        if (current == null && children.isEmpty())
            item { Text("Nessuna categoria sul PC.", color = TextDim, modifier = Modifier.padding(24.dp)) }
    }
}

/** The categories along [path] that still exist, from the top level down. */
internal fun resolvePath(roots: List<CategoryDto>, path: List<String>): List<CategoryDto> {
    val trail = mutableListOf<CategoryDto>()
    var level = roots
    for (id in path) {
        val next = level.firstOrNull { it.id == id } ?: break
        trail += next
        level = next.children
    }
    return trail
}

// ---- Search ----------------------------------------------------------------------------------

@Composable
private fun SearchTab(library: StateDto, playingIds: Set<String>, onPlay: OnPlay) {
    var query by rememberSaveable { mutableStateOf("") }
    val results = remember(library, query) {
        val all = library.sounds.sortedBy { it.title.lowercase() }
        val text = query.trim()
        if (text.isEmpty()) all
        else all.filter { it.title.contains(text, ignoreCase = true) || it.artist.contains(text, ignoreCase = true) }
    }
    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Cerca tra ${library.sounds.size} suoni") },
            leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(12.dp),
        )
        LazyColumn(Modifier.fillMaxSize()) {
            items(results, key = { it.id }) { sound -> SoundRow(sound, sound.id in playingIds, onPlay) }
            if (results.isEmpty())
                item { Text("Nessun suono trovato.", color = TextDim, modifier = Modifier.padding(24.dp)) }
        }
    }
}

// ---- Now playing -----------------------------------------------------------------------------

@Composable
private fun NowPlayingBar(
    state: UiState,
    soundsById: Map<String, SoundDto>,
    actions: RemoteActions,
    showMic: Boolean = true,
) {
    val playback = state.playback
    val sounds = playback.sounds

    // The PC sends positions only on changes; extrapolate them while playing.
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(playback, state.playbackReceivedAt) {
        while (playback.state == "playing") {
            now = SystemClock.elapsedRealtime()
            delay(200)
        }
    }
    val elapsed = if (playback.state == "playing") now - state.playbackReceivedAt else 0L

    Surface(color = Panel) {
        Column(Modifier.fillMaxWidth()) {
            HorizontalDivider()
            Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                // One bar per sound playing, side by side; each disappears when its sound ends or is stopped.
                Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (sounds.isEmpty()) {
                        PlayingRow("Nessun suono in riproduzione", 0f, active = false, onStop = null, Modifier.weight(1f))
                    }
                    sounds.forEach { sound ->
                        // A sound paused on its own stands still while the others move on.
                        val position = sound.positionMs + if (sound.paused) 0 else elapsed
                        val fraction = if (sound.durationMs > 0) (position.toFloat() / sound.durationMs).coerceIn(0f, 1f) else 0f
                        PlayingRow(
                            sound.soundId?.let { soundsById[it]?.title }.orEmpty(),
                            fraction,
                            active = true,
                            onStop = if (sound.id.isNotEmpty()) ({ actions.stopOne(sound.id) }) else null,
                            paused = sound.paused,
                            onPause = if (sound.id.isNotEmpty()) ({ actions.togglePauseOne(sound.id) }) else null,
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
                IconButton(onClick = actions::togglePause, enabled = playback.isActive) {
                    if (playback.isPaused) Icon(Icons.Filled.PlayArrow, contentDescription = "Riprendi")
                    else PauseIcon()
                }
                IconButton(onClick = actions::stop) { StopIcon() }
            }
            if (showMic) {
                Row(Modifier.horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                    MicChip(state.mic, onToggle = { actions.setMic(!state.mic.enabled) })
                    EffectChip(state.effect, onPick = actions::setEffect)
                    FilterChip(
                        selected = state.effectTarget != EffectTarget.Off,
                        onClick = { actions.setEffectTarget(state.effectTarget.next) },
                        label = { Text(state.effectTarget.label) },
                        colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
                        modifier = Modifier.padding(start = 8.dp, bottom = 6.dp),
                    )
                    Knob(
                        state.effectAmount,
                        enabled = state.effect != SoundEffect.None,
                        onChange = actions::setEffectAmount,
                        modifier = Modifier.padding(start = 10.dp, bottom = 6.dp),
                    )
                    Text(
                        "${state.effectAmount}%",
                        color = TextDim,
                        fontSize = 12.sp,
                        modifier = Modifier.padding(start = 6.dp, bottom = 6.dp),
                    )
                    var pad by remember { mutableStateOf(false) }
                    FilterChip(
                        selected = state.playbackSpeed != 1.0,
                        onClick = { pad = true },
                        label = { Text("Pad ×${"%.2f".format(state.playbackSpeed)}") },
                        colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
                        modifier = Modifier.padding(start = 8.dp, bottom = 6.dp),
                    )
                    if (pad) {
                        PadDialog(state.effect, state.effectAmount, state.playbackSpeed, onChange = actions::setPad, onDismiss = { pad = false })
                    }
                }
            }
        }
    }
}

@Composable
private fun PlayingRow(
    title: String,
    fraction: Float,
    active: Boolean,
    onStop: (() -> Unit)?,
    modifier: Modifier = Modifier,
    paused: Boolean = false,
    onPause: (() -> Unit)? = null,
) {
    Row(modifier.padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(
                title,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                color = if (active) MaterialTheme.colorScheme.onSurface else TextDim,
            )
            Spacer(Modifier.height(4.dp))
            LinearProgressIndicator(
                progress = { fraction },
                modifier = Modifier.fillMaxWidth(),
                color = if (paused) TextDim else Gold,
                trackColor = Panel2,
                drawStopIndicator = {},
            )
        }
        if (onPause != null) {
            IconButton(onClick = onPause, modifier = Modifier.size(36.dp)) {
                if (paused) Icon(Icons.Filled.PlayArrow, contentDescription = "Riprendi questo suono", tint = TextDim, modifier = Modifier.size(20.dp))
                else PauseIcon(Modifier.size(18.dp), tint = TextDim)
            }
        }
        if (onStop != null) {
            IconButton(onClick = onStop, modifier = Modifier.size(36.dp)) {
                Icon(Icons.Filled.Close, contentDescription = "Ferma questo suono", tint = TextDim, modifier = Modifier.size(18.dp))
            }
        }
    }
}

/** The voice effect the PC plays the sounds through; a tap opens the list. */
@Composable
private fun EffectChip(effect: SoundEffect, onPick: (SoundEffect) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box(Modifier.padding(start = 8.dp, bottom = 6.dp)) {
        FilterChip(
            selected = effect != SoundEffect.None,
            onClick = { open = true },
            label = { Text(if (effect == SoundEffect.None) "Effetto: nessuno" else "Effetto: ${effect.label}") },
            colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
        )
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            SoundEffect.entries.forEach { option ->
                DropdownMenuItem(
                    text = { Text(option.label, color = if (option == effect) Gold else Color.Unspecified) },
                    onClick = {
                        open = false
                        onPick(option)
                    },
                )
            }
        }
    }
}

@Composable
private fun MicChip(mic: MicDto, onToggle: () -> Unit) {
    val label = when {
        !mic.enabled -> "Microfono: spento"
        mic.state == "active" -> "Microfono: attivo"
        mic.state == "idle" -> "Microfono: in attesa"
        mic.state == "notInstalled" -> "Microfono: non installato sul PC"
        else -> "Microfono: da reinstallare sul PC"
    }
    FilterChip(
        selected = mic.enabled && mic.state == "active",
        onClick = onToggle,
        label = { Text(label) },
        colors = FilterChipDefaults.filterChipColors(selectedContainerColor = GoldSoft, selectedLabelColor = Gold),
        modifier = Modifier.padding(start = 12.dp, bottom = 6.dp),
    )
}
