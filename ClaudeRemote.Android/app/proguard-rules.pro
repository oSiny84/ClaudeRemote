# ClaudeRemote ProGuard Rules

# Keep Kotlin Serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt

-keepclassmembers class kotlinx.serialization.json.** {
    *** Companion;
}
-keepclasseswithmembers class kotlinx.serialization.json.** {
    kotlinx.serialization.KSerializer serializer(...);
}

# Keep data model classes
-keep class com.clauderemote.data.model.** { *; }

# OkHttp
-dontwarn okhttp3.**
-dontwarn okio.**
