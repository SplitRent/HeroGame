using System.Collections.Generic;
using System.Text;

namespace HeroGame.Core.Foundation
{
    public enum Severity
    {
        Info,
        Warning,
        Error,
    }

    public struct ValidationMessage
    {
        public Severity Severity;
        public string Path;
        public string Message;

        public override string ToString() => Severity + " [" + Path + "] " + Message;
    }

    /// <summary>Collects validation output from config, content and save validation.</summary>
    public sealed class ValidationReport
    {
        public readonly List<ValidationMessage> Messages = new List<ValidationMessage>();

        public bool HasErrors
        {
            get
            {
                foreach (var m in Messages) if (m.Severity == Severity.Error) return true;
                return false;
            }
        }

        public void Info(string path, string message) => Add(Severity.Info, path, message);
        public void Warn(string path, string message) => Add(Severity.Warning, path, message);
        public void Error(string path, string message) => Add(Severity.Error, path, message);

        private void Add(Severity severity, string path, string message)
        {
            Messages.Add(new ValidationMessage { Severity = severity, Path = path, Message = message });
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var m in Messages) sb.AppendLine(m.ToString());
            return sb.ToString();
        }
    }

    /// <summary>Result of attempting an operation that may be rejected by validation.</summary>
    public readonly struct OpResult
    {
        public readonly bool Success;
        public readonly string Error;

        private OpResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public static OpResult Ok() => new OpResult(true, null);
        public static OpResult Fail(string error) => new OpResult(false, error);
        public override string ToString() => Success ? "OK" : "FAILED: " + Error;
    }
}
