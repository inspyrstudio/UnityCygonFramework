// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using UnityEngine;

namespace InspyrStudio.CygonLink
{
    /// <summary>Central logger for Cygon Link: prefixes and colors messages sent to the Unity console.</summary>
    public static class EditorRuntime_USDA
    {
        public const string logPrefix = "Cygon Link";

        /// <summary>Logs a colored, prefixed message to the Unity console.</summary>
        /// <param name="color">Rich-text color name or hex (e.g. "green", "#ff8800").</param>
        /// <param name="message">The message body.</param>
        public static void SendLog(string color, string message)
        {
            Debug.Log($"<b>{logPrefix}</b>: <color={color}>{message}</color>");
        }
    }
}
