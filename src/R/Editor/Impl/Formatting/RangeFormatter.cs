﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.Common.Core.Services;
using Microsoft.Languages.Core.Formatting;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Editor.ContainedLanguage;
using Microsoft.Languages.Editor.Formatting;
using Microsoft.Languages.Editor.Selection;
using Microsoft.Languages.Editor.Services;
using Microsoft.Languages.Editor.Text;
using Microsoft.R.Core.AST;
using Microsoft.R.Core.Formatting;
using Microsoft.R.Core.Parser;
using Microsoft.R.Core.Tokens;
using Microsoft.R.Editor.Document;
using Microsoft.R.Editor.SmartIndent;

namespace Microsoft.R.Editor.Formatting {
    public sealed class RangeFormatter {
        private readonly IServiceContainer _services;
        private readonly IREditorSettings _settings;

        public RangeFormatter(IServiceContainer services) {
            _services = services;
            _settings = services.GetService<IREditorSettings>();
        }

        public bool FormatRange(IEditorView editorView, IEditorBuffer editorBuffer, ITextRange formatRange) {
            var snapshot = editorBuffer.CurrentSnapshot;
            var start = formatRange.Start;
            var end = formatRange.End;

            if (!CanFormatRange(editorView, editorBuffer, formatRange)) {
                return false;
            }

            // When user clicks editor margin to select a line, selection actually
            // ends in the beginning of the next line. In order to prevent formatting
            // of the next line that user did not select, we need to shrink span to
            // format and exclude the trailing line break.
            var line = snapshot.GetLineFromPosition(formatRange.End);

            if (line.Start == formatRange.End && formatRange.Length > 0) {
                if (line.LineNumber > 0) {
                    line = snapshot.GetLineFromLineNumber(line.LineNumber - 1);
                    end = line.End;
                    start = Math.Min(start, end);
                }
            }

            // Expand span to include the entire line
            var startLine = snapshot.GetLineFromPosition(start);
            var endLine = snapshot.GetLineFromPosition(end);

            // In case of formatting of multiline expressions formatter needs
            // to know the entire expression since otherwise it may not correctly
            // preserve user indentation. Consider 'x >% y' which is a plain statement
            // and needs to be indented at regular scope level vs
            //
            //      a %>% b %>%
            //          x %>% y
            //
            // where user indentation of 'x %>% y' must be preserved. We don't have
            // complete information here since expression may not be syntactically
            // correct and hence AST may not have correct information and besides,
            // the AST is damaged at this point. As a workaround, we will check 
            // if the previous line ends with an operator current line starts with 
            // an operator.
            int startPosition = FindStartOfExpression(editorBuffer, startLine.Start);

            formatRange = TextRange.FromBounds(startPosition, endLine.End);
            return FormatRangeExact(editorView, editorBuffer, formatRange);
        }

        private bool FormatRangeExact(IEditorView editorView, IEditorBuffer editorBuffer, ITextRange formatRange) {
            var snapshot = editorBuffer.CurrentSnapshot;
            var spanText = snapshot.GetText(formatRange);
            var trimmedSpanText = spanText.Trim();

            var formatter = new RFormatter(_settings.FormatOptions);
            var formattedText = formatter.Format(trimmedSpanText);

            formattedText = formattedText.Trim(); // There may be inserted line breaks after {
            // Apply formatted text without indentation. We then will update the parse tree 
            // so we can calculate proper line indents from the AST via the smart indenter.
            if (!spanText.Equals(formattedText, StringComparison.Ordinal)) {
                // Extract existing indent before applying changes. Existing indent
                // may be used by the smart indenter for function argument lists.
                var startLine = snapshot.GetLineFromPosition(formatRange.Start);
                var originalIndentSizeInSpaces = IndentBuilder.TextIndentInSpaces(startLine.GetText(), _settings.IndentSize);

                var selectionTracker = GetSelectionTracker(editorView, editorBuffer, formatRange);
                var tokenizer = new RTokenizer();
                var oldTokens = tokenizer.Tokenize(spanText);
                var newTokens = tokenizer.Tokenize(formattedText);

                var wsChangeHandler = _services.GetService<IIncrementalWhitespaceChangeHandler>();
                wsChangeHandler.ApplyChange(
                    editorBuffer,
                    new TextStream(spanText), new TextStream(formattedText),
                    oldTokens, newTokens,
                    formatRange,
                    Microsoft.R.Editor.Resources.AutoFormat, selectionTracker,
                    () => {
                        var ast = UpdateAst(editorBuffer);
                        // Apply indentation
                        IndentLines(editorBuffer, new TextRange(formatRange.Start, formattedText.Length), ast, originalIndentSizeInSpaces);
                    });

                return true;
            }

            return false;
        }

        private AstRoot UpdateAst(IEditorBuffer editorBuffer) {
            var document = editorBuffer.GetEditorDocument<IREditorDocument>();
            if (document != null) {
                document.EditorTree.EnsureTreeReady();
                return document.EditorTree.AstRoot;
            }
            return RParser.Parse(editorBuffer.CurrentSnapshot);
        }

        /// <summary>
        /// Appends indentation to each line so formatted text appears properly 
        /// indented inside the host document (script block in HTML page).
        /// </summary>
        private void IndentLines(IEditorBuffer textBuffer, ITextRange range, AstRoot ast, int originalIndentSizeInSpaces) {
            var snapshot = textBuffer.CurrentSnapshot;
            var firstLine = snapshot.GetLineFromPosition(range.Start);
            var lastLine = snapshot.GetLineFromPosition(range.End);
            var document = textBuffer.GetEditorDocument<IREditorDocument>();

            for (var i = firstLine.LineNumber; i <= lastLine.LineNumber; i++) {
                // Snapshot is updated after each insertion so do not cache
                var line = textBuffer.CurrentSnapshot.GetLineFromLineNumber(i);
                var indent = SmartIndenter.GetSmartIndent(line, _settings, ast, originalIndentSizeInSpaces, formatting: true);
                if (indent > 0 && line.Length > 0 && line.Start >= range.Start) {
                    // Check current indentation and correct for the difference
                    int currentIndentSize = IndentBuilder.TextIndentInSpaces(line.GetText(), _settings.TabSize);
                    indent = Math.Max(0, indent - currentIndentSize);
                    if (indent > 0) {
                        var indentString = IndentBuilder.GetIndentString(indent, _settings.IndentType, _settings.TabSize);
                        textBuffer.Insert(line.Start, indentString);
                        if (document == null) {
                            // Typically this is a test scenario only. In the real editor
                            // instance document is available and automatically updates AST
                            // when whitespace inserted, not no manual update is necessary.
                            ast.ReflectTextChange(line.Start, 0, indentString.Length, textBuffer.CurrentSnapshot);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Given position in the buffer tries to detemine start of the expression.
        /// </summary>
        private int FindStartOfExpression(IEditorBuffer textBuffer, int position) {
            // Go up line by line, tokenize each line
            // and check if it starts or ends with an operator
            var lineNum = textBuffer.CurrentSnapshot.GetLineNumberFromPosition(position);
            var tokenizer = new RTokenizer(separateComments: true);

            var text = textBuffer.CurrentSnapshot.GetLineFromLineNumber(lineNum).GetText();
            var tokens = tokenizer.Tokenize(text);
            var nextLineStartsWithOperator = tokens.Count > 0 && tokens[0].TokenType == RTokenType.Operator;

            for (var i = lineNum - 1; i >= 0; i--) {
                var line = textBuffer.CurrentSnapshot.GetLineFromLineNumber(i);
                text = line.GetText();
                tokens = tokenizer.Tokenize(text);

                if (tokens.Count > 0) {
                    if (!nextLineStartsWithOperator && tokens[tokens.Count - 1].TokenType != RTokenType.Operator) {
                        break;
                    }
                    position = tokens[0].Start + line.Start;
                    nextLineStartsWithOperator = tokens[0].TokenType == RTokenType.Operator;
                }
            }

            return position;
        }

        private bool CanFormatRange(IEditorView editorView, IEditorBuffer editorBuffer, ITextRange formatRange) {
            // Make sure we are not formatting damaging the projected range in R Markdown
            // which looks like ```{r. 'r' should not separate from {.
            var locator = _services.GetService<IContentTypeServiceLocator>();
            var provider = locator.GetService<IContainedLanguageHostProvider>("R");
            var host = provider?.GetContainedLanguageHost(editorView, editorBuffer);
            if (host != null) {
                var snapshot = editorBuffer.CurrentSnapshot;
                var startLine = snapshot.GetLineNumberFromPosition(formatRange.Start);
                var endLine = snapshot.GetLineNumberFromPosition(formatRange.End);
                for (var i = startLine; i <= endLine; i++) {
                    if (!host.CanFormatLine(editorView, editorBuffer, i)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private ISelectionTracker GetSelectionTracker(IEditorView editorView, IEditorBuffer editorBuffer, ITextRange formatRange) {
            var locator = _services.GetService<IContentTypeServiceLocator>();
            var provider = locator?.GetService<ISelectionTrackerProvider>(editorBuffer.ContentType);
            return provider?.CreateSelectionTracker(editorView, editorBuffer, formatRange) ?? new DefaultSelectionTracker(editorView);
        }

        private sealed class DefaultSelectionTracker: ISelectionTracker {
            public DefaultSelectionTracker(IEditorView editorView) {
                EditorView = editorView;
            }

            public IEditorView EditorView { get; }
            public void StartTracking(bool automaticTracking) { }
            public void EndTracking() { }
            public void MoveToBeforeChanges() { }
            public void MoveToAfterChanges(int virtualSpaces = 0) { }
        }
    }
}
