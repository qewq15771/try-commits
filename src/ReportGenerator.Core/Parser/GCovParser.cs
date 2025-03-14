using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for reports generated by gcov (See: https://github.com/linux-test-project/lcov, http://ltp.sourceforge.net/coverage/lcov/geninfo.1.php).
    /// </summary>
    internal class GCovParser : ParserBase
    {
        /// <summary>
        /// Text content in first line.
        /// </summary>
        public const string SourceElementInFirstLine = "0:Source:";

        /// <summary>
        /// Regex to analyze if a line contains line coverage data.
        /// </summary>
        private static readonly Regex LineCoverageRegex = new Regex("\\s*(?<Visits>-|#####|=====|\\d+):\\s*(?<LineNumber>[1-9]\\d*):.*", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a line contains branch coverage data.
        /// </summary>
        private static readonly Regex BranchCoverageRegex = new Regex("branch\\s*(?<Number>\\d+)\\s*(?:taken\\s*(?<Visits>\\d+)|never\\sexecuted?)", RegexOptions.Compiled);

        /// <summary>
        /// The default assembly name.
        /// </summary>
        private readonly string defaultAssemblyName = "Default";

        /// <summary>
        /// Initializes a new instance of the <see cref="GCovParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        public GCovParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GCovParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        /// <param name="defaultAssemblyName">The default assembly name.</param>
        public GCovParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter, string defaultAssemblyName)
            : base(assemblyFilter, classFilter, fileFilter)
        {
            this.defaultAssemblyName = defaultAssemblyName;
        }

        /// <summary>
        /// Parses the given report.
        /// </summary>
        /// <param name="lines">The report lines.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(string[] lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            var assembly = new Assembly(this.defaultAssemblyName);
            var assemblies = new List<Assembly>()
            {
                assembly
            };

            if (lines.Length > 0)
            {
                this.ProcessClass(assembly, lines);
            }

            // Not every GCov file contains branch coverage
            bool supportsBranchCoverage = assembly.Classes.Any(c => c.TotalBranches.GetValueOrDefault() > 0);
            var result = new ParserResult(assemblies, supportsBranchCoverage, this.ToString());
            return result;
        }

        private void ProcessClass(Assembly assembly, string[] lines)
        {
            string fileName = lines[0].Substring(lines[0].IndexOf(SourceElementInFirstLine) + GCovParser.SourceElementInFirstLine.Length);

            if (!this.FileFilter.IsElementIncludedInReport(fileName))
            {
                return;
            }

            string className = fileName.Substring(fileName.LastIndexOf(Path.DirectorySeparatorChar) + 1);

            if (!this.ClassFilter.IsElementIncludedInReport(className))
            {
                return;
            }

            var @class = new Class(fileName, assembly);

            this.ProcessCoverage(@class, fileName, lines);

            assembly.AddClass(@class);
        }

        private void ProcessCoverage(Class @class, string fileName, string[] lines)
        {
            var codeElements = new List<CodeElementBase>();
            int maxiumLineNumber = -1;
            var visitsByLine = new Dictionary<int, int>();

            var branchesByLineNumber = new Dictionary<int, ICollection<Branch>>();

            foreach (var line in lines)
            {
                var match = LineCoverageRegex.Match(line);

                if (match.Success)
                {
                    int lineNumber = int.Parse(match.Groups["LineNumber"].Value, CultureInfo.InvariantCulture);
                    maxiumLineNumber = Math.Max(maxiumLineNumber, lineNumber);

                    string visitsText = match.Groups["Visits"].Value;

                    if (visitsText != "-")
                    {
                        int visits = 0;

                        if (visitsText != "#####" && visitsText != "=====")
                        {
                            visits = visitsText.ParseLargeInteger();
                        }

                        if (visitsByLine.ContainsKey(lineNumber))
                        {
                            visitsByLine[lineNumber] += visits;
                        }
                        else
                        {
                            visitsByLine[lineNumber] = visits;
                        }
                    }
                }
                else
                {
                    match = BranchCoverageRegex.Match(line);

                    if (match.Success)
                    {
                        var branch = new Branch(
                            match.Groups["Visits"].Success ? match.Groups["Visits"].Value.ParseLargeInteger() : 0,
                            match.Groups["Number"].Value);

                        if (branchesByLineNumber.TryGetValue(maxiumLineNumber, out ICollection<Branch> branches))
                        {
                            HashSet<Branch> branchesHashset = (HashSet<Branch>)branches;
                            if (branchesHashset.Contains(branch))
                            {
                                // Not perfect for performance, but Hashset has no GetElement method
                                branchesHashset.First(b => b.Equals(branch)).BranchVisits += branch.BranchVisits;
                            }
                            else
                            {
                                branches.Add(branch);
                            }
                        }
                        else
                        {
                            branches = new HashSet<Branch>
                            {
                                branch
                            };

                            branchesByLineNumber.Add(maxiumLineNumber, branches);
                        }
                    }
                    else if (line.StartsWith("function "))
                    {
                        string name = line.Substring(9, line.IndexOf(' ', 9) - 9);

                        codeElements.Add(new CodeElementBase(name, maxiumLineNumber + 1));
                    }
                }
            }

            int[] coverage = new int[maxiumLineNumber + 1];
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[maxiumLineNumber + 1];

            for (int i = 0; i < coverage.Length; i++)
            {
                coverage[i] = -1;
            }

            foreach (var kv in visitsByLine)
            {
                coverage[kv.Key] = kv.Value;

                if (lineVisitStatus[kv.Key] != LineVisitStatus.Covered)
                {
                    bool partiallyCovered = false;

                    if (branchesByLineNumber.TryGetValue(kv.Key, out ICollection<Branch> branchesOfLine))
                    {
                        partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                    }

                    LineVisitStatus statusOfLine = kv.Value > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                    lineVisitStatus[kv.Key] = (LineVisitStatus)Math.Max((int)lineVisitStatus[kv.Key], (int)statusOfLine);
                }
            }

            var codeFile = new CodeFile(fileName, coverage, lineVisitStatus, branchesByLineNumber);

            for (int i = 0; i < codeElements.Count; i++)
            {
                var codeElement = codeElements[i];

                int lastLine = maxiumLineNumber;
                if (i < codeElements.Count - 1)
                {
                    lastLine = codeElements[i + 1].FirstLine - 1;
                }

                codeFile.AddCodeElement(new CodeElement(
                    codeElement.Name,
                    CodeElementType.Method,
                    codeElement.FirstLine,
                    lastLine,
                    codeFile.CoverageQuotaInRange(codeElement.FirstLine, lastLine)));
            }

            @class.AddFile(codeFile);
        }
    }
}
Add auth - fixing typo