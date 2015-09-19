﻿using System;
using System.Threading;

namespace Microsoft.Common.Core.Disposables
{
    public sealed class CountdownDisposable
    {
        private readonly Action _disposeAction;
        private int _count;

        public int Count => this._count;

        public CountdownDisposable(Action disposeAction = null)
        {
            this._disposeAction = disposeAction ?? (() => { });
        }

        public IDisposable Increment()
        {
            Interlocked.Increment(ref _count);
            return Disposable.Create(Decrement);
        }

        private void Decrement()
        {
            if (Interlocked.Decrement(ref _count) == 0)
            {
                this._disposeAction();
            }
        }
    }
}
