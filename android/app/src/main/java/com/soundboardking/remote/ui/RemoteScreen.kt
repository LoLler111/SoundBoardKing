package com.soundboardking.remote.ui

import android.os.SystemClock
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import com.soundboardking.remote.data.MicDto
import com.soundboardking.remote.data.PlaybackDto
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
fun RemoteScreen(state: UiState, actions: RemoteActions, snackbar: SnackbarHostState) =
    RemoteScreen(state, actions, snackbar, Tab.Favorites)

@Composable
internal fun RemoteScreen(state: UiState, actions: RemoteActions, snackbar: SnackbarHostState, startTab: Tab) {
    KeepScreenOn()
    var tab by rememberSaveable { mutableStateOf(startTab) }
    val library = state.library
    val soundsById = remember(library) { library?.sounds?.associateBy { it.id }.orEmpty() }
    val playingId = state.playback.soundId.takeIf { state.playback.isActive }

    Scaffold(
        topBar = { RemoteTopBar(state, onRefresh = actions::reconnect, onForget = actions::forgetServer) },
        bottomBar = {
            Column {
                NowPlayingBar(state, soundsById, actions)
                NavigationBar(containerColor = Panel) {
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
                when (tab) {
                    Tab.Favorites -> FavoritesTab(library, soundsById, playingId, actions::play)
                    Tab.Categories -> CategoriesTab(library, soundsById, playingId, actions::play)
                    Tab.Search -> SearchTab(library, playingId, actions::play)
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RemoteTopBar(state: UiState, onRefresh: () -> Unit, onForget: () -> Unit) {
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
            IconButton(onClick = onRefresh) { Icon(Icons.Filled.Refresh, contentDescription = "Riconnetti") }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "Altro") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(
                        text = { Text("Abbina un altro PC") },
                        onClick = {
                            menu = false
                            onForget()
                        },
                    )
                }
            }
        },
    )
}

// ---- Favourites ------------------------------------------------------------------------------

@Composable
private fun FavoritesTab(library: StateDto, soundsById: Map<String, SoundDto>, playingId: String?, onPlay: (String) -> Unit) {
    val hotbar = library.hotbar
    val pagerState = rememberPagerState { hotbar.pages }
    val scope = rememberCoroutineScope()
    val slots = remember(library) { hotbar.slots.associateBy { it.page to it.slot } }

    Column(Modifier.fillMaxSize()) {
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
        HorizontalPager(state = pagerState, modifier = Modifier.weight(1f)) { page ->
            LazyVerticalGrid(
                columns = GridCells.Fixed(4),
                modifier = Modifier.fillMaxSize().padding(horizontal = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                items(hotbar.slotsPerPage) { slot ->
                    val sound = slots[page to slot]?.let { soundsById[it.soundId] }
                    SlotTile(slot + 1, sound, playing = sound != null && sound.id == playingId, onPlay)
                }
            }
        }
    }
}

@Composable
private fun SlotTile(number: Int, sound: SoundDto?, playing: Boolean, onPlay: (String) -> Unit) {
    val haptic = LocalHapticFeedback.current
    Surface(
        shape = RoundedCornerShape(10.dp),
        color = if (playing) GoldSoft else Panel2,
        border = if (playing) BorderStroke(1.dp, Gold) else null,
        modifier = Modifier
            .aspectRatio(1f)
            .alpha(if (sound == null) 0.35f else if (sound.missing) 0.55f else 1f)
            .clickable(enabled = sound != null) {
                haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                sound?.let { onPlay(it.id) }
            },
    ) {
        Box(Modifier.padding(6.dp)) {
            Text("$number", fontSize = 10.sp, color = TextDim, modifier = Modifier.align(Alignment.TopStart))
            Text(
                sound?.title.orEmpty(),
                fontSize = 12.sp,
                lineHeight = 14.sp,
                textAlign = TextAlign.Center,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.align(Alignment.Center).padding(top = 6.dp),
            )
        }
    }
}

// ---- Categories ------------------------------------------------------------------------------

@Composable
private fun CategoriesTab(library: StateDto, soundsById: Map<String, SoundDto>, playingId: String?, onPlay: (String) -> Unit) {
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
        items(sounds, key = { "s" + it.id }) { sound -> SoundRow(sound, sound.id == playingId, onPlay) }
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
private fun SearchTab(library: StateDto, playingId: String?, onPlay: (String) -> Unit) {
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
            items(results, key = { it.id }) { sound -> SoundRow(sound, sound.id == playingId, onPlay) }
            if (results.isEmpty())
                item { Text("Nessun suono trovato.", color = TextDim, modifier = Modifier.padding(24.dp)) }
        }
    }
}

// ---- Now playing -----------------------------------------------------------------------------

@Composable
private fun NowPlayingBar(state: UiState, soundsById: Map<String, SoundDto>, actions: RemoteActions) {
    val playback = state.playback
    val title = playback.soundId?.let { soundsById[it]?.title }.takeIf { playback.isActive }

    // The PC sends the position only on changes; extrapolate it while playing.
    var now by remember { mutableLongStateOf(SystemClock.elapsedRealtime()) }
    LaunchedEffect(playback, state.playbackReceivedAt) {
        while (playback.state == "playing") {
            now = SystemClock.elapsedRealtime()
            delay(200)
        }
    }
    val position = if (playback.state == "playing") playback.positionMs + (now - state.playbackReceivedAt) else playback.positionMs
    val fraction = if (playback.durationMs > 0) (position.toFloat() / playback.durationMs).coerceIn(0f, 1f) else 0f

    Surface(color = Panel) {
        Column(Modifier.fillMaxWidth()) {
            HorizontalDivider()
            Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(
                        title ?: "Nessun suono in riproduzione",
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        fontWeight = if (title != null) FontWeight.SemiBold else FontWeight.Normal,
                        color = if (title != null) MaterialTheme.colorScheme.onSurface else TextDim,
                    )
                    Spacer(Modifier.height(6.dp))
                    LinearProgressIndicator(
                        progress = { fraction },
                        modifier = Modifier.fillMaxWidth(),
                        color = Gold,
                        trackColor = Panel2,
                        drawStopIndicator = {},
                    )
                }
                IconButton(onClick = actions::togglePause, enabled = playback.isActive) {
                    if (playback.isPaused) Icon(Icons.Filled.PlayArrow, contentDescription = "Riprendi")
                    else PauseIcon()
                }
                IconButton(onClick = actions::stop) { StopIcon() }
            }
            MicChip(state.mic, onToggle = { actions.setMic(!state.mic.enabled) })
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
