import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
    // Renders Compose screens to PNG on the JVM (no device needed): gradlew recordRoborazziDebug
    id("io.github.takahirom.roborazzi")
}

// One version for the Windows app and this APK, from version.txt at the repository root.
// 1.2.3 -> versionCode 10203, so every release installs over the previous one.
val appVersion = rootDir.resolve("../version.txt").readText().trim()
val appVersionCode = appVersion.split('.').map { it.toInt() }.let { (major, minor, patch) -> major * 10000 + minor * 100 + patch }

// Release signing key, never in the repository: the release workflow passes it in SBK_* variables
// (from the GitHub secrets), a local build reads ~/.soundboardking/signing.properties.
val signing = Properties().apply {
    val file = File(System.getProperty("user.home"), ".soundboardking/signing.properties")
    if (file.exists()) file.inputStream().use(::load)
    System.getenv("SBK_KEYSTORE")?.let { setProperty("storeFile", it) }
    System.getenv("SBK_KEYSTORE_PASSWORD")?.let { setProperty("storePassword", it) }
    System.getenv("SBK_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
    System.getenv("SBK_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
}
val hasReleaseKey = signing.getProperty("storeFile")?.let { File(it).exists() } == true

android {
    namespace = "com.soundboardking.remote"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.soundboardking.remote"
        minSdk = 26
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersion
        // Where the in-app update looks for the latest release; -PupdateUrl=... points a test build elsewhere.
        val updateUrl = providers.gradleProperty("updateUrl")
            .getOrElse("https://api.github.com/repos/LoLler111/SoundBoardKing/releases/latest")
        buildConfigField("String", "LATEST_RELEASE_URL", "\"$updateUrl\"")
    }

    signingConfigs {
        if (hasReleaseKey) create("release") {
            storeFile = File(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // The in-app update installs a new APK over the old one only when both carry the same
            // signature: releases always use the release key. Without it (a PC that never had the
            // key) the APK is signed with the debug key and cannot update a released one.
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2025.08.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.2")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.2")

    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
    testImplementation("org.robolectric:robolectric:4.17")
    testImplementation("io.github.takahirom.roborazzi:roborazzi:1.76.0")
    testImplementation("io.github.takahirom.roborazzi:roborazzi-compose:1.76.0")
    testImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
