using System;
using Android.Runtime;
using AndroidX.Activity.Result;

namespace BotwExtractor
{
    // The AndroidX binding exposes ActivityResultCallback as a Java interface, so
    // this adapter lets activities register plain C# lambdas with the Activity
    // Result API instead of overriding the deprecated OnActivityResult path.
    internal sealed class ResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        readonly Action<Java.Lang.Object> _onResult;

        public ResultCallback(Action<Java.Lang.Object> onResult) => _onResult = onResult;

        public void OnActivityResult(Java.Lang.Object result) => _onResult?.Invoke(result);
    }

    internal static class ActivityResults
    {
        // Results arrive as Java objects; cast through JNI so the managed wrapper
        // matches the runtime type (Uri for the document/tree/create contracts).
        public static Android.Net.Uri AsUri(this Java.Lang.Object result) =>
            result == null ? null : result.JavaCast<Android.Net.Uri>();

        public static string AsString(this Java.Lang.Object result) =>
            (result as Java.Lang.String)?.ToString();
    }
}
