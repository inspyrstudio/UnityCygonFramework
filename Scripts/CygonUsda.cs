// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace InspyrStudio.CygonLink
{
    /// <summary>
    /// Shared helpers for identifying / locating Cygon USDA files and converting absolute
    /// disk paths to Unity asset paths. Lives in the shared "misc" assembly so both the
    /// editor (importer/processor) and the runtime sync can use it without duplication.
    /// </summary>
    public static class CygonUsda
    {        
        //=============================================================================
        // VARIABLES
        //=============================================================================

        #region CONSTANTS

        /// <summary>First line that marks a USDA file as produced by the Cygon exporter.</summary>
        public const string Header = "#usda 1.0 | Cygon";

        #endregion



        //=============================================================================
        // HELPERS
        //=============================================================================

        #region FILE IDENTIFICATION

        /// <summary>Checks whether a file is a Cygon USDA file.</summary>
        /// <param name="path">Path to the file to test.</param>
        /// <returns>True when the file exists and its first line equals <see cref="Header"/>.</returns>
        public static bool IsCygonFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            return File.ReadLines(path).FirstOrDefault() == Header;
        }

        /// <summary>Scans the project's Assets folder for Cygon USDA files.</summary>
        /// <returns>The absolute paths of every Cygon USDA file found under Assets.</returns>
        public static List<string> FindAll()
        {
            List<string> result = new List<string>();
            foreach (string full in Directory.GetFiles(Application.dataPath, "*.usda", SearchOption.AllDirectories))
            {
                if (IsCygonFile(full)) result.Add(full);
            }
            return result;
        }

        #endregion

        #region PATHS

        /// <summary>Converts an absolute path under Assets/ to a Unity project-relative asset path.</summary>
        /// <param name="absolutePath">Absolute disk path, typically under the project's Assets folder.</param>
        /// <returns>The "Assets/…"-relative path, or the input unchanged if it is not under Assets.</returns>
        public static string ToAssetPath(string absolutePath)
        {
            string p = absolutePath.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            return p.StartsWith(data) ? "Assets" + p.Substring(data.Length) : p;
        }

        #endregion


    }
}
