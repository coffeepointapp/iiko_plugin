using System;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Minimal <see cref="IObserver{T}"/> adapter so we can subscribe to the Resto
    /// SDK's plain BCL <see cref="IObservable{T}"/> notification streams (e.g.
    /// OrderChanged) with a lambda/method group — the SDK exposes only the
    /// <c>Subscribe(IObserver&lt;T&gt;)</c> overload, and we deliberately avoid taking
    /// a System.Reactive dependency inside a host-loaded plugin DLL.
    /// </summary>
    internal sealed class ActionObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public ActionObserver(Action<T> onNext)
        {
            _onNext = onNext ?? throw new ArgumentNullException(nameof(onNext));
        }

        public void OnNext(T value) => _onNext(value);
        public void OnError(Exception error) { /* stream errors are non-fatal for us */ }
        public void OnCompleted() { }
    }
}
