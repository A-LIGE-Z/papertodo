import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

val releaseKeys = Properties().apply {
    val source = rootProject.file("key.properties")
    if (source.isFile) source.inputStream().use { load(it) }
}
fun signingValue(environment: String, property: String): String? =
    System.getenv(environment)?.takeIf { it.isNotBlank() } ?: releaseKeys.getProperty(property)?.takeIf { it.isNotBlank() }
val keystorePath = signingValue("PAPERTODO_KEYSTORE_PATH", "storeFile")
val keystorePassword = signingValue("PAPERTODO_KEYSTORE_PASSWORD", "storePassword")
val signingAlias = signingValue("PAPERTODO_KEY_ALIAS", "keyAlias")
val signingPassword = signingValue("PAPERTODO_KEY_PASSWORD", "keyPassword")
val signingReady = listOf(keystorePath, keystorePassword, signingAlias, signingPassword).all { it != null }
val unsignedValidation = providers.gradleProperty("papertodoUnsignedValidation").orNull == "true"
val legacyMigration = providers.gradleProperty("papertodoLegacyMigration").orNull == "true"
val releaseValidation = providers.gradleProperty("papertodoReleaseValidation").orNull == "true"
val automationTest = providers.gradleProperty("papertodoAutomationTest").orNull == "true"
require(listOf(unsignedValidation, legacyMigration, releaseValidation).count { it } <= 1) { "Choose only one non-production validation/migration mode" }
if (gradle.startParameter.taskNames.any { it.contains("release", ignoreCase = true) } &&
    !signingReady && !unsignedValidation && !legacyMigration && !releaseValidation) {
    throw GradleException("Formal release signing is required. Configure android/key.properties or PAPERTODO_KEYSTORE_PATH, PAPERTODO_KEY_ALIAS, PAPERTODO_KEYSTORE_PASSWORD and PAPERTODO_KEY_PASSWORD. Unsigned validation and legacy migration builds must be requested explicitly.")
}

android {
    namespace = "com.papertodo.mobile"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "com.papertodo.mobile"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
        testInstrumentationRunner = "com.papertodo.mobile.CompanionTestRunner"
        manifestPlaceholders["appLabel"] = "PaperTodo"
    }

    signingConfigs {
        if (signingReady) create("formalRelease") {
            storeFile = rootProject.file(keystorePath!!)
            storePassword = keystorePassword
            keyAlias = signingAlias
            keyPassword = signingPassword
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = if (automationTest) ".integration" else ".dev"
            versionNameSuffix = "-dev"
            manifestPlaceholders["appLabel"] = if (automationTest) "PaperTodo 自动化测试" else "PaperTodo 测试"
        }
        release {
            signingConfig = when {
                releaseValidation -> signingConfigs.getByName("debug")
                legacyMigration -> signingConfigs.getByName("debug")
                signingReady -> signingConfigs.getByName("formalRelease")
                else -> null
            }
            if (legacyMigration) manifestPlaceholders["appLabel"] = "PaperTodo 数据迁移"
            if (releaseValidation) {
                // Keep JVM symbols available to the separate instrumentation APK;
                // Dart still uses the production AOT compiler in this build.
                isMinifyEnabled = false
                isShrinkResources = false
                applicationIdSuffix = if (automationTest) ".integration" else ".dev"
                versionNameSuffix = "-validation"
                manifestPlaceholders["appLabel"] = if (automationTest) "PaperTodo 自动化测试" else "PaperTodo 测试"
            }
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
