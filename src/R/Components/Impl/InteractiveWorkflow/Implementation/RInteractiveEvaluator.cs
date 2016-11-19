﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Disposables;
using Microsoft.Common.Core.IO;
using Microsoft.Common.Core.Shell;
using Microsoft.R.Components.ConnectionManager;
using Microsoft.R.Components.History;
using Microsoft.R.Components.Settings;
using Microsoft.R.Core.AST;
using Microsoft.R.Core.Parser;
using Microsoft.R.Host.Client;
using Microsoft.R.Host.Client.Host;
using Microsoft.R.Host.Client.Session;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;

namespace Microsoft.R.Components.InteractiveWorkflow.Implementation {
    public sealed class RInteractiveEvaluator : IInteractiveEvaluator {
        private readonly IRSessionProvider _sessionProvider;
        private readonly IConnectionManager _connections;
        private readonly ICoreShell _coreShell;
        private readonly IRSettings _settings;
        private readonly CountdownDisposable _evaluatorRequest;
        private int _terminalWidth = 80;

        public IRHistory History { get; }
        public IRSession Session { get; }

        public RInteractiveEvaluator(IRSessionProvider sessionProvider, IRSession session, IRHistory history, IConnectionManager connections, ICoreShell coreShell, IRSettings settings) {
            History = history;
            Session = session;
            Session.Output += SessionOnOutput;
            Session.Disconnected += SessionOnDisconnected;
            Session.BeforeRequest += SessionOnBeforeRequest;
            Session.AfterRequest += SessionOnAfterRequest;
            _sessionProvider = sessionProvider;
            _connections = connections;
            _coreShell = coreShell;
            _settings = settings;
            _evaluatorRequest = new CountdownDisposable();
        }

        public void Dispose() {
            if (CurrentWindow != null) {
                CurrentWindow.TextView.VisualElement.SizeChanged -= VisualElement_SizeChanged;
            }

            Session.Output -= SessionOnOutput;
            Session.Disconnected -= SessionOnDisconnected;
            Session.BeforeRequest -= SessionOnBeforeRequest;
            Session.AfterRequest -= SessionOnAfterRequest;
        }

        public async Task<ExecutionResult> InitializeAsync() {
            return await InitializeAsync(false);
        }

        private async Task<ExecutionResult> InitializeAsync(bool isResetting) {
            try {
                if (!Session.IsHostRunning) {
                    var startupInfo = new RHostStartupInfo {
                        Name = "REPL",
                        RHostCommandLineArguments = _connections.ActiveConnection?.RCommandLineArguments,
                        CranMirrorName = _settings.CranMirror,
                        CodePage = _settings.RCodePage,
                        WorkingDirectory = _settings.WorkingDirectory,
                        TerminalWidth = _terminalWidth,
                        EnableAutosave = !isResetting
                    };

                    if (CurrentWindow != null) {
                        CurrentWindow.TextView.VisualElement.SizeChanged += VisualElement_SizeChanged;
                        CurrentWindow.OutputBuffer.Changed += OutputBuffer_Changed;
                    }

                    await Session.EnsureHostStartedAsync(startupInfo, new RSessionCallback(CurrentWindow, Session, _settings, _coreShell, new FileSystem()));
                }
                return ExecutionResult.Success;
            } catch (RHostBrokerBinaryMissingException) {
                await _coreShell.ShowErrorMessageAsync(Resources.Error_Microsoft_R_Host_Missing);
                return ExecutionResult.Failure;
            } catch (RHostDisconnectedException ex) {
                WriteRHostDisconnectedError(ex);
                return ExecutionResult.Success;
            } catch (Exception ex) {
                await _coreShell.ShowErrorMessageAsync(ex.Message);
                return ExecutionResult.Failure;
            }
        }

        public async Task<ExecutionResult> ResetAsync(bool initialize = true) {
            try {
                CurrentWindow.TextView.VisualElement.SizeChanged -= VisualElement_SizeChanged;
                if (Session.IsHostRunning) {
                    CurrentWindow.WriteError(Resources.MicrosoftRHostStopping + Environment.NewLine);
                    await Session.StopHostAsync();
                }

                if (!initialize) {
                    return ExecutionResult.Success;
                }

                CurrentWindow.WriteError(Environment.NewLine + Resources.MicrosoftRHostStarting + Environment.NewLine);
                return await InitializeAsync(true);
            } catch (Exception ex) {
                Trace.Fail($"Exception in RInteractiveEvaluator.ResetAsync\n{ex}");
                return ExecutionResult.Failure;
            }
        }

        public bool CanExecuteCode(string text) {
            if (text.StartsWith("?", StringComparison.Ordinal)) {
                return true;
            }

            // if we have any errors other than an incomplete statement send the
            // bad code to R.  Otherwise continue reading input.
            var ast = RParser.Parse(text);
            return ast.IsCompleteExpression();
        }

        public async Task<ExecutionResult> ExecuteCodeAsync(string text) {
            var start = 0;
            var end = text.IndexOf('\n');
            if (end == -1) {
                return ExecutionResult.Success;
            }

            try {
                using (Session.DisableMutatedOnReadConsole()) {
                    while (end != -1) {
                        var line = text.Substring(start, end - start + 1);
                        start = end + 1;
                        end = text.IndexOf('\n', start);

                        using (var request = await Session.BeginInteractionAsync()) {
                            using (_evaluatorRequest.Increment()) {
                                if (line.Length >= request.MaxLength) {
                                    CurrentWindow.WriteErrorLine(string.Format(Resources.InputIsTooLong, request.MaxLength));
                                    return ExecutionResult.Failure;
                                }

                                await request.RespondAsync(line);
                            }
                        }
                    }
                }

                return ExecutionResult.Success;
            } catch (RHostDisconnectedException rhdex) {
                WriteRHostDisconnectedError(rhdex);
                return ExecutionResult.Success;
            } catch (OperationCanceledException) {
                // Cancellation reason was already reported via RSession.Error and printed out;
                // Return success cause connection lost doesn't mean that RHost died
                return ExecutionResult.Success;
            } catch (Exception ex) {
                await _coreShell.ShowErrorMessageAsync(ex.ToString());
                return ExecutionResult.Failure;
            } finally {
                History.AddToHistory(text);
            }
        }

        public string FormatClipboard() {
            // keep the clipboard content as is
            return null;
        }

        public void AbortExecution() {
            Session.CancelAllAsync().DoNotWait();
        }

        public string GetPrompt() {
            if (CurrentWindow.CurrentLanguageBuffer.CurrentSnapshot.LineCount > 1) {
                // TODO: We should support dynamically getting the prompt at runtime
                // if the user changes it
                return "+ ";
            }
            return Session.Prompt;
        }

        public IInteractiveWindow CurrentWindow { get; set; }

        private void SessionOnOutput(object sender, ROutputEventArgs args) {
            if (args.OutputType == OutputType.Output) {
                Write(args.Message.ToUnicodeQuotes());
            } else {
                WriteError(args.Message);
            }
        }

        private void SessionOnDisconnected(object sender, EventArgs args) {
            if (CurrentWindow == null || !CurrentWindow.IsResetting) {
                WriteError(Resources.MicrosoftRHostStopped);
            }
        }

        private void SessionOnAfterRequest(object sender, RAfterRequestEventArgs e) {
            if (_evaluatorRequest.Count == 0 && e.AddToHistory && e.IsVisible) {
                _coreShell.DispatchOnUIThread(() => {
                    if (CurrentWindow == null || CurrentWindow.IsResetting) {
                        return;
                    }

                    ((IInteractiveWindow2)CurrentWindow).AddToHistory(e.Request.TrimEnd());
                    History.AddToHistory(e.Request);
                });
            }
        }

        private void SessionOnBeforeRequest(object sender, RBeforeRequestEventArgs e) {
            _coreShell.DispatchOnUIThread(() => {
                if (CurrentWindow == null || CurrentWindow.IsRunning) {
                    return;
                }

                var projectionBuffer = CurrentWindow.TextView.TextBuffer as IProjectionBuffer;
                if (projectionBuffer == null) {
                    return;
                }

                var spanCount = projectionBuffer.CurrentSnapshot.SpanCount;
                projectionBuffer.ReplaceSpans(spanCount - 2, 1, new List<object> { GetPrompt() }, EditOptions.None, new object());
            });
        }

        /// <summary>
        /// Workaround for interactive window that does not currently support
        /// 'carriage return' i.e. writing into the same line
        /// </summary>
        struct MessagePos {
            public string Message;
            public int Position;
        }

        private readonly Stack<MessagePos> _messageStack = new Stack<MessagePos>();
        private void OutputBuffer_Changed(object sender, TextContentChangedEventArgs e) {
            if (e.After.Length - e.Before.Length == 1) {
                _coreShell.AssertIsOnMainThread();
                while (_messageStack.Count > 0) {
                    var mp = _messageStack.Pop();
                    // Writing messages in the same line (via simulated CR)

                    var ch = CurrentWindow.OutputBuffer.CurrentSnapshot[mp.Position];
                    Debug.Assert(ch == '$');
                    Debug.Assert(mp.Position + 1 == CurrentWindow.OutputBuffer.CurrentSnapshot.Length);

                    // Replace last written placeholder with the actual message
                    CurrentWindow.OutputBuffer.Replace(new Span(mp.Position, 1), mp.Message);
                }
            }
        }

        private void Write(string message) {
            if (CurrentWindow != null) {
                // Note: DispatchOnUIThread is expensive, and can saturate the message pump when there's a lot of output,
                // making UI non-responsive. So avoid using it unless we need it - and we only need it for FlushOutput,
                // and we only need it to handle CR.
                if (message.Length > 1 && message[0] == '\r' && message[1] != '\n') {
                    _coreShell.DispatchOnUIThread(() => {
                        CurrentWindow.FlushOutput();
                        // If message starts with CR we remember current output buffer
                        // length so we can continue writing lines into the same spot.
                        // See txtProgressBar in R.
                        if (message.Length > 1 && message[0] == '\r' && message[1] != '\n') {
                            // Store the message and the initial position. All subsequent 
                            // messages that start with CR. Will be written into the same place.
                            var mp = new MessagePos() {
                                Message = message.Substring(1),
                                Position = CurrentWindow.OutputBuffer.CurrentSnapshot.Length
                            };
                            message = "$"; // replacement placeholder so we can receive 'buffer changed' event
                            _messageStack.Push(mp);
                        }
                        CurrentWindow.Write(message);
                        CurrentWindow.FlushOutput(); // Must flush so we do get 'buffer changed' immediately.
                    });
                } else {
                    CurrentWindow.Write(message);
                }
            }
        }

        private void WriteError(string message) {
            if (CurrentWindow != null) {
                _coreShell.DispatchOnUIThread(() => CurrentWindow.WriteError(message));
            }
        }

        private void WriteRHostDisconnectedError(RHostDisconnectedException exception) {
            WriteRHostDisconnectedErrorAsync(exception).DoNotWait();
        }

        private async Task WriteRHostDisconnectedErrorAsync(RHostDisconnectedException exception) {
            await _coreShell.SwitchToMainThreadAsync();
            if (CurrentWindow != null) {
                CurrentWindow.WriteErrorLine(exception.Message);
                CurrentWindow.WriteErrorLine(_sessionProvider.IsConnected ? Resources.RestartRHost : Resources.ReconnectToBroker);
            }
        }

        private void VisualElement_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e) {
            int width = (int)(e.NewSize.Width / CurrentWindow.TextView.FormattedLineSource.ColumnWidth);
            // From R docs:  Valid values are 10...10000 with default normally 80.
            _terminalWidth = Math.Max(10, Math.Min(10000, width));

            Session.OptionsSetWidthAsync(_terminalWidth)
                .SilenceException<RException>()
                .DoNotWait();
        }
    }
}