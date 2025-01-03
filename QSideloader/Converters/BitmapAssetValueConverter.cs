﻿using System;
using System.Globalization;
using System.Reflection;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace QSideloader.Converters;

/// <summary>
///     <para>
///         Converts a string path to a bitmap asset.
///     </para>
///     <para>
///         The asset must be in the same assembly as the program. If it isn't,
///         specify "avares://assemblynamehere/" in front of the path to the asset.
///     </para>
/// </summary>
public class BitmapAssetValueConverter : IValueConverter
{
    public static readonly BitmapAssetValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return null;
            case string rawUri when targetType.IsAssignableFrom(typeof(Bitmap)):
            {
                Uri uri;

                // Allow for assembly overrides
                if (rawUri.StartsWith("avares://"))
                {
                    uri = new Uri(rawUri);
                }
                else
                {
                    var assemblyName = Assembly.GetEntryAssembly()!.GetName().Name!;
                    uri = new Uri($"avares://{assemblyName}{rawUri}");
                }

                return new Bitmap(AssetLoader.Open(uri));
            }
            default:
                throw new NotSupportedException();
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}