﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace Microsoft.Common.Core.UI.Commands {
    public class Command : ICommand {
        readonly bool _needCheckout;
        readonly CommandId[] _commandIds;

        public Command(Guid group, int id, bool needCheckout)
            : this(new[] { new CommandId(group, id) }, needCheckout) {
        }

        public Command(CommandId id, bool needCheckout)
            : this(new[] { id }, needCheckout) {
        }

        public Command(CommandId id)
            : this(id, false) {
        }

        public Command(int id, bool needCheckout)
            : this(new CommandId(id), needCheckout) {
        }

        public Command(CommandId[] ids, bool needCheckout) {
            _commandIds = ids;
            _needCheckout = needCheckout;
        }

        public Command(Guid group, int[] ids) {
            _commandIds = new CommandId[ids.Length];
            for (int i = 0; i < ids.Length; i++) {
                _commandIds[i] = new CommandId(group, ids[i]);
            }

            _needCheckout = false;
        }

        #region ICommand
        public virtual bool NeedCheckout(Guid group, int id) => _needCheckout;
        public virtual IList<CommandId> CommandIds => _commandIds;
        public virtual CommandStatus Status(Guid group, int id) => CommandStatus.NotSupported;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference", MessageId = "3#")]
        public virtual CommandResult Invoke(Guid group, int id, object inputArg, ref object outputArg) => CommandResult.NotSupported;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference", MessageId = "4#")]
        public virtual void PostProcessInvoke(CommandResult result, Guid group, int id, object inputArg, ref object outputArg) {
        }
        #endregion
    }
}
