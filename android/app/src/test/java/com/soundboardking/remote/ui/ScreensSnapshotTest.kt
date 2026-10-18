package com.soundboardking.remote.ui

import androidx.compose.material3.SnackbarHostState
import com.github.takahirom.roborazzi.captureRoboImage
import com.soundboardking.remote.data.CategoryDto
import com.soundboardking.remote.data.DiscoveredPc
import com.soundboardking.remote.data.FavoritesLook
import com.soundboardking.remote.data.DiscoveryReply
import com.soundboardking.remote.data.HotbarDto
import com.soundboardking.remote.data.HotbarSlotDto
import com.soundboardking.remote.data.MicDto
import com.soundboardking.remote.data.PlayTarget
import com.soundboardking.remote.data.PlaybackDto
import com.soundboardking.remote.data.PlayingDto
import com.soundboardking.remote.data.SavedServer
import com.soundboardking.remote.data.SoundDto
import com.soundboardking.remote.data.StateDto
import com.soundboardking.remote.ui.theme.SoundBoardTheme
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** Renders the screens with sample data: gradlew recordRoborazziDebug writes them to src/test/snapshots. */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-xxhdpi")
class ScreensSnapshotTest {

    private val actions = object : RemoteActions, ConnectActions {
        override fun play(soundId: String, target: PlayTarget) {}
        override fun stop() {}
        override fun stopOne(playbackId: String) {}
        override fun togglePause() {}
        override fun setMic(enabled: Boolean) {}
        override fun reconnect() {}
        override fun forgetServer() {}
        override fun setLook(look: FavoritesLook) {}
        override fun discover() {}
        override fun pairWithLink(text: String) {}
        override fun pairWithDiscovered(pc: DiscoveredPc, code: String) {}
        override fun pairManually(address: String, code: String) {}
        override fun showMessage(message: String) {}
    }

    private val titles = listOf(
        "airhorn", "56k_modem", "applauso", "BENSON-questa-volta-no", "Canto del gallo", "dramatic",
        "Il cane pufoente", "rutto", "SQUALO 73 (CROP)", "Tokyo Drift Teriyaki Boyz", "urlo-del-sium-cristiano-ronaldo",
        "Sono LiLLo", "HF_Brava", "Camalow_camalow", "bamboleio", "batman-change-scene",
    )
    private val sounds = titles.mapIndexed { i, title ->
        SoundDto("s$i", title, durationMs = (i + 1) * 3_100L, missing = i == 7)
    }
    private val library = StateDto(
        serverId = "pc",
        name = "PC-SALOTTO",
        protocol = 1,
        libraryVersion = 1,
        sounds = sounds,
        categories = listOf(
            CategoryDto("voice", "Voice", sounds.take(5).map { it.id }, listOf(CategoryDto("teorema", "Teorema", listOf("s1")))),
            CategoryDto("music", "Musica", sounds.drop(5).map { it.id }),
            CategoryDto("earrape", "Earrape"),
        ),
        hotbar = HotbarDto(slots = sounds.take(14).mapIndexed { i, s -> HotbarSlotDto(0, if (i < 12) i else i + 3, s.id) }),
    )
    private val connected = UiState(
        server = SavedServer("pc", "PC-SALOTTO", listOf("192.168.1.10"), 8765, "t"),
        connection = ConnectionState.Online,
        library = library,
        playback = PlaybackDto(
            "playing", "s3", 400, 12_400,
            listOf(PlayingDto("p1", "s1", 1_200, 6_200), PlayingDto("p2", "s3", 400, 12_400)),
        ),
        mic = MicDto("active", true),
    )

    @Test
    fun favorites() = captureRoboImage("src/test/snapshots/favorites.png") {
        SoundBoardTheme { RemoteScreen(connected, actions, SnackbarHostState(), Tab.Favorites) }
    }

    @Test
    fun favoritesBig() = captureRoboImage("src/test/snapshots/favorites-big.png") {
        val big = connected.copy(look = FavoritesLook(columns = 3, fontSp = 17))
        SoundBoardTheme { RemoteScreen(big, actions, SnackbarHostState(), Tab.Favorites) }
    }

    @Test
    fun favoritesTiny() = captureRoboImage("src/test/snapshots/favorites-tiny.png") {
        val tiny = connected.copy(look = FavoritesLook(columns = 12, fontSp = 6))
        SoundBoardTheme { RemoteScreen(tiny, actions, SnackbarHostState(), Tab.Favorites) }
    }

    @Test
    fun favoritesWide() = captureRoboImage("src/test/snapshots/favorites-wide.png") {
        val wide = connected.copy(look = FavoritesLook(columns = 4, fontSp = 12, shape = 3))
        SoundBoardTheme { RemoteScreen(wide, actions, SnackbarHostState(), Tab.Favorites) }
    }

    @Test
    @Config(qualifiers = "w1280dp-h800dp-land-hdpi")
    fun tablet() = captureRoboImage("src/test/snapshots/tablet.png") {
        SoundBoardTheme { RemoteScreen(connected, actions, SnackbarHostState(), Tab.Favorites) }
    }

    @Test
    @Config(qualifiers = "w1280dp-h800dp-land-hdpi")
    fun tabletFullScreen() = captureRoboImage("src/test/snapshots/tablet-fullscreen.png") {
        val look = connected.copy(look = FavoritesLook(columns = 8, fontSp = 13, shape = 2))
        SoundBoardTheme { RemoteScreen(look, actions, SnackbarHostState(), Tab.Favorites, startFullScreen = true) }
    }

    @Test
    fun fullScreen() = captureRoboImage("src/test/snapshots/fullscreen.png") {
        SoundBoardTheme { RemoteScreen(connected, actions, SnackbarHostState(), Tab.Favorites, startFullScreen = true) }
    }

    @Test
    fun lookPreview() = captureRoboImage("src/test/snapshots/look-dialog.png") {
        SoundBoardTheme { LookDialog(FavoritesLook(columns = 3, fontSp = 17, shape = 2), onChange = {}, onDismiss = {}) }
    }

    @Test
    fun categories() = captureRoboImage("src/test/snapshots/categories.png") {
        SoundBoardTheme { RemoteScreen(connected.copy(playback = PlaybackDto(), mic = MicDto("idle", true)), actions, SnackbarHostState(), Tab.Categories) }
    }

    @Test
    fun search() = captureRoboImage("src/test/snapshots/search.png") {
        SoundBoardTheme { RemoteScreen(connected.copy(mic = MicDto("notInstalled", false)), actions, SnackbarHostState(), Tab.Search) }
    }

    @Test
    fun offline() = captureRoboImage("src/test/snapshots/offline.png") {
        SoundBoardTheme {
            RemoteScreen(
                connected.copy(connection = ConnectionState.Offline, library = null, connectionMessage = "PC non raggiungibile."),
                actions, SnackbarHostState(), Tab.Favorites,
            )
        }
    }

    @Test
    fun pairing() = captureRoboImage("src/test/snapshots/pairing.png") {
        SoundBoardTheme {
            ConnectScreen(
                UiState(discovered = listOf(DiscoveredPc("192.168.1.10", DiscoveryReply("SoundBoardKing", 1, "PC-SALOTTO", 8765, "pc")))),
                actions, SnackbarHostState(),
            )
        }
    }
}
