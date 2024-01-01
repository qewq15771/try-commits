using System;

namespace Palmmedia.ReportGenerator.Core.Logging
{
    /// <summary>
    /// A logger factory creating console loggers.
    /// </summary>
    internal class ConsoleLoggerFactory : ILoggerFactory
    {
        /// <summary>
        /// The cached logger.
        /// </summary>
        private readonly ILogger logger = new ConsoleLogger();

        /// <summary>
        /// Gets or sets the verbosity of console loggers.
        /// </summary>
        public VerbosityLevel VerbosityLevel
        {
            get
            {
                return this.logger.VerbosityLevel;
            }

            set
            {
                this.logger.VerbosityLevel = value;
            }
        }

        /// <summary>
        /// Initializes the logger for the given type.
        /// </summary>
        /// <param name="type">The type of the class that uses the logger.</param>
        /// <returns>The logger.</returns>
        public ILogger GetLogger(Type type) => this.logger;
    }
}
