﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Threading.Tasks;

namespace Microsoft.Azure.Functions.Worker.Converters
{
    public interface IConverter
    {
        ValueTask<BindingResult> ConvertAsync(ConverterContext context);
    }
}
