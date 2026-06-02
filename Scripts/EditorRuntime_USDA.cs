// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using UnityEngine;

namespace InspyrStudio.CygonLink
{
    public static class EditorRuntime_USDA
    {
        public static string logPrefix = "Cygon Link";

        public static void SendLog(string color,  string message)
        {
            Debug.Log($"<b>{logPrefix}</b>: <color={color}>{message}</color>");
        }
    }
}
