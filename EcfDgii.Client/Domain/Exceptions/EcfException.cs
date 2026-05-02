using System;

namespace EcfDgii.Client.Domain.Exceptions
{
    public class EcfException : Exception
    {
        public object? Response { get; }

        public EcfException(string message) : base(message) { }

        public EcfException(string message, object? response)
            : base(message)
        {
            Response = response;
        }

        public EcfException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class EcfSigningException : EcfException
    {
        public EcfSigningException(string message) : base(message) { }
    }

    public class PollingMaxRetriesException : Exception
    {
        public int Retries { get; }

        public PollingMaxRetriesException(int retries)
            : base($"Polling exceeded maximum retries ({retries})")
        {
            Retries = retries;
        }
    }

    public class PollingTimeoutException : TimeoutException
    {
        public PollingTimeoutException(string message = "Polling timed out")
            : base(message) { }
    }
}
