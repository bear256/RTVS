﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Languages.Editor.DragDrop;
using Microsoft.R.Host.Client.Extensions;
using static System.FormattableString;

namespace Microsoft.R.Editor.DragDrop {
    public static class DataObject {
        public static string GetPlainText(this IDataObject dataObject, string projectFolder) {
            var flags = dataObject.GetFlags();
            if ((flags & DataObjectFlags.ProjectItems) != 0) {
                return dataObject.TextFromProjectItems(projectFolder);
            }
            return string.Empty;
        }

        private static string TextFromProjectItems(this IDataObject dataObject, string projectFolder) {
            var sb = new StringBuilder();
            foreach(var item in dataObject.GetProjectItems()) {
                var relative = item.FileName.MakeRRelativePath(projectFolder);
                var ext = Path.GetExtension(item.FileName).ToLowerInvariant();
                switch(ext) {
                    case ".r":
                        sb.AppendLine(Invariant($"{Environment.NewLine}source('{relative}')"));
                        break;
                    case ".sql":
                        sb.Append(Invariant($"'{GetFileContent(item.FileName)}'"));
                        break;
                    default:
                        sb.Append(Invariant($"'{relative}'"));
                        break;
                }
            }
            return sb.ToString();
        }

        private static string GetFileContent(string file) {
            try {
                using (var sr = new StreamReader(file)) {
                    return sr.ReadToEnd().Trim();
                }
            } catch(IOException) { } catch(AccessViolationException) { }
            return string.Empty;
        }
    }
}
