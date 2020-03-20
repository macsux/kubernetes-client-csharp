using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace k8s.Tests.Utils
{
    public class XunitLogger<T> : ILogger<T>, IDisposable
    {
        private ITestOutputHelper _output;

        public XunitLogger(ITestOutputHelper output)
        {
            _output = output;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            _output.WriteLine($"{DateTime.Now.Ticks - Current} | {state} | ThreadID: {Thread.CurrentThread.ManagedThreadId}");
        }

        private static long Current = DateTime.Now.Ticks;

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this;
        }

        public void Dispose()
        {
        }
    }
}
