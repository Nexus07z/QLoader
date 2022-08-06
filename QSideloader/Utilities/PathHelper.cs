﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace QSideloader.Utilities;

public static class PathHelper
{
    static PathHelper()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AdbPath = @".\tools\windows\platform-tools\adb.exe";
            RclonePath = @".\tools\windows\rclone\FFA.exe";
            SevenZipPath = Path.Combine(@".\tools\windows", Environment.Is64BitProcess ? "x64" : "x86", "7za.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var architectureString = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new NotImplementedException("Unsupported architecture")
            };
            AdbPath = Path.Combine("./tools/linux/", architectureString, "platform-tools/adb");
            RclonePath = Path.Combine("./tools/linux/", architectureString, "rclone/FFA");
            SevenZipPath = Path.Combine("./tools/linux/", architectureString, "7zz");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var architectureString = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new NotImplementedException("Unsupported architecture")
            };
            AdbPath = @"./tools/darwin/platform-tools/adb";
            RclonePath = @"./tools/darwin/rclone/FFA";
            RclonePath = Path.Combine("./tools/darwin/", architectureString, "rclone/FFA");
            SevenZipPath = @"./tools/darwin/7zz";
        }
    }

    public static string AdbPath { get; } = "";
    public static string RclonePath { get; } = "";
    public static string SevenZipPath { get; } = "";
    public static string SettingsPath => "settings.json";
    public static string ThumbnailsPath => Path.Combine("Resources", "thumbnails");
    public static string TrailersPath => Path.Combine("Resources", "videos");
    public static string DefaultDownloadsPath => Path.Combine(Environment.CurrentDirectory, "downloads");
    public static string DefaultBackupsPath => Path.Combine(Environment.CurrentDirectory, "backups");

    // Source: https://stackoverflow.com/a/55480402
    public static string GetActualCaseForFileName(string pathAndFileName)
    {
        var directory = Path.GetDirectoryName(pathAndFileName) ??
                        throw new InvalidOperationException("Path is not valid");
        var pattern = Path.GetFileName(pathAndFileName);
        string resultFileName;

        // Enumerate all files in the directory, using the file name as a pattern
        // This will list all case variants of the filename even on file systems that
        // are case sensitive
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive
        };
        var foundFiles = Directory.EnumerateFiles(directory, pattern, options).ToList();

        if (foundFiles.Any())
        {
            if (foundFiles.Count > 1)
                // More than two files with the same name but different case spelling found
                throw new Exception("Ambiguous File reference for " + pathAndFileName);

            resultFileName = foundFiles.First();
        }
        else
        {
            throw new FileNotFoundException("File not found " + pathAndFileName, pathAndFileName);
        }

        return resultFileName;
    }
}