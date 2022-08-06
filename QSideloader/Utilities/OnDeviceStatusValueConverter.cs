﻿using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace QSideloader.Utilities;

public class OnDeviceStatusValueConverter : IValueConverter
{
    public static OnDeviceStatusValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null;

        if (value is not bool status) throw new NotSupportedException();
        return status ? "On Device" : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
            return status switch
            {
                "On Device" => true,
                "" => false,
                _ => throw new NotSupportedException()
            };
        throw new NotSupportedException();
    }
}