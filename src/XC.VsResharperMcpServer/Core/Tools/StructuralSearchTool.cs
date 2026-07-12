using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Annotations;
using JetBrains.Application.Threading;
using JetBrains.DocumentManagers;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CSharp.StructuralSearch;
using JetBrains.ReSharper.Feature.Services.StructuralSearch;
using JetBrains.ReSharper.Feature.Services.StructuralSearch.Impl;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // M8 (not from the reference repo - see docs/DEVNOTES.md). Structural Search, and now Structural
    // Replace too.
    //
    // SEARCH MODE: LIVE-TESTED (2026-07-12, see docs/DEVNOTES.md) - literal (placeholder-free) patterns
    // and "$name$"-placeholder patterns both confirmed correct (placeholders confirmed to bind whole
    // compound sub-expressions, not just identifiers - a genuinely non-trivial correctness signal).
    // Required calling ssrPattern.GuessPlaceholders() explicitly after construction -
    // CSharpStructuralSearchPattern.CreateMatcher() alone always builds its matcher-builder array with
    // guessPlaceholders: false and hard-fails (returns null) on any undeclared "$name$" token;
    // GuessPlaceholders() is the SDK's own separate, public step that resolves each one by trying a
    // SEPARATE guessPlaceholders: true builder array - found live via a real failing test, fixed, then
    // confirmed fixed by the same test.
    //
    // PSI-LOCK WEDGE HANG - ROOT-CAUSED AND FIXED (2026-07-12, see docs/DEVNOTES.md "structural_search
    // hang - root cause and fix"): certain patterns (e.g. "$x$.GetHashCode()") permanently wedged the
    // PSI lock, same failure class as the earlier apply_quick_fix hang. Root cause (confirmed via
    // decompilation, not guessed): the SDK's own StructuralSearchRequest.SearchReplaceTargets() always
    // drives its domain traversal through StructuralSearchDomainSearcher<TResult>, which derives from
    // SearchDomainVisitorParallel - an ASYNC multi-threaded fan-out (PsiTaskBarrier.CreateRead with
    // sync: false) designed for an interactive, message-pumped host thread. Called from this plugin's
    // headless dispatch thread, the fanned-out per-file jobs never get scheduled and the wait blocks
    // forever. Fix: SearchReplaceTargetsSequential() below reimplements the same search using the
    // plain, synchronous SearchDomainVisitor base class instead - see its doc comment for the full
    // decompilation trail. No SDK/locking behavior was changed; only which traversal entry point this
    // tool calls.
    //
    // REPLACE MODE: added by extending this same tool rather than a separate one, since it reuses 100%
    // of the search-domain/pattern-construction logic above - only the final step differs (mutate
    // instead of report). LIVE-TESTED (2026-07-12, see docs/DEVNOTES.md): a placeholder-based method-call
    // rename ("$x$.OldMethod()" -> "$x$.NewMethod()") confirmed correct on the first try, dry run and
    // real apply both clean, no hang, 0 diagnostics on the result - the cleanest first-attempt result of
    // any write tool built this session. JetBrains.ReSharper.Feature.Services.CSharp.
    // StructuralSearch.CSharpStructuralSearchReplacer.Replace(IEnumerable<IStructuralMatchResult>
    // results, string replacePattern, IDictionary<string, IPlaceholder> placeholders, bool
    // formatAfterReplace, bool shortenReferences) is a real, public, static method (decompiled directly)
    // that takes the exact same IList<IStructuralMatchResult> SearchReplaceTargets already returns for
    // search mode, plus the replace pattern text and the SAME Placeholders dictionary already populated
    // by GuessPlaceholders() during the search phase (so a "$x$" in the replace pattern binds to
    // whatever "$x$" matched during search, per match) - no second placeholder-resolution pass needed.
    // Dispatches via ExecuteSelfTransactingWrite (this mutates PSI, unlike search) with a real
    // PsiTransactionCookie (temporary/rollback for dryRun, auto-commit otherwise) - matching every other
    // write tool's proven-safe pattern. Does not touch DaemonHighlightingCollector/DoHighlighting
    // anywhere (confirmed both by its decompiled body and by this live test never hanging), so it's
    // outside the apply_suggestions hang-risk category. Only one specific pattern shape has actually
    // been exercised live - more elaborate replace patterns remain unverified.
    // dryRun defaults to true for replace mode specifically (unlike search, which has no concept of
    // dryRun) - a caller must explicitly pass dryRun=false to actually mutate anything, a safer default
    // given only one specific pattern shape has been live-tested so far.
    //
    // CAUTION, stronger than most of this session's other tools even after live-testing search:
    // compiling cleanly means LESS here than for a typical tool. Every type/constructor/method used is
    // confirmed real via decompilation, so the WIRING is solid - but whether any given SSR pattern
    // string actually parses and matches (or replaces) correctly against real code is only as verified
    // as the specific patterns actually tried. A pattern that compiles fine at the C# level (this tool's
    // own code) can still silently match nothing, match everything, or produce a wrong replacement for a
    // pattern shape this tool has never actually exercised - none of that would show up as a compile
    // error. Treat any output from this tool with real skepticism until run live against known patterns
    // with known expected results.
    public class StructuralSearchTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly DocumentManager _documentManager;

        public StructuralSearchTool(ISolution solution, IShellLocks shellLocks, DocumentManager documentManager)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _documentManager = documentManager;
        }

        public string Execute(string pattern, string replacement = null, string filePath = null, int maxResults = 50, bool dryRun = true)
        {
            if (string.IsNullOrEmpty(replacement))
            {
                return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.structural_search", () =>
                    ExecuteSearch(pattern, filePath, maxResults));
            }

            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.structural_search.replace", () =>
                ExecuteReplace(pattern, replacement, filePath, dryRun));
        }

        private (CSharpStructuralSearchPattern pattern, ISearchDomain domain, string error) BuildRequest(string pattern, string filePath)
        {
            if (string.IsNullOrEmpty(pattern))
                return (null, null, "pattern is required (e.g. \"$expr$.ToString()\" - see ReSharper's own Structural " +
                       "Search and Replace syntax; $name$ placeholders match any sub-element).");

            var searchDomainFactory = _solution.GetPsiServices().SearchDomainFactory;

            ISearchDomain searchDomain;
            if (!string.IsNullOrEmpty(filePath))
            {
                var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
                if (sourceFile == null)
                    return (null, null, $"File not found in solution: {filePath}");
                searchDomain = searchDomainFactory.CreateSearchDomain(sourceFile);
            }
            else
            {
                searchDomain = searchDomainFactory.CreateSearchDomain(_solution, includeLibraries: false);
            }

            var ssrPattern = new CSharpStructuralSearchPattern(pattern);

            // See class doc comment for why this call is required - a bare CreateMatcher() call
            // without it hard-fails on any undeclared "$name$" placeholder token.
            ssrPattern.GuessPlaceholders();

            return (ssrPattern, searchDomain, null);
        }

        private string ExecuteSearch(string pattern, string filePath, int maxResults)
        {
            var (ssrPattern, searchDomain, error) = BuildRequest(pattern, filePath);
            if (error != null) return error;

            IList<IStructuralMatchResult> matches;
            try
            {
                matches = SearchReplaceTargetsSequential(ssrPattern, searchDomain);
            }
            catch (Exception ex)
            {
                return $"structural_search failed - the pattern likely couldn't be parsed: {ex.Message}";
            }

            if (matches == null)
                return "Search did not complete (cancelled, or the pattern was rejected before matching started).";

            if (matches.Count == 0)
                return $"No matches found for pattern: {pattern}";

            var sb = new StringBuilder();
            sb.Append("structural_search: ").Append(matches.Count).Append(" match(es) for pattern: ").AppendLine(pattern);

            var shown = 0;
            foreach (var match in matches)
            {
                if (shown >= maxResults)
                {
                    sb.AppendLine().Append("... (truncated, ").Append(matches.Count - maxResults).Append(" more not shown)");
                    break;
                }

                var element = match.MatchedElement;
                if (element == null) continue;

                var sourceFile = element.GetSourceFile();
                var path = sourceFile?.GetLocation().FullPath ?? "(unknown file)";

                sb.AppendLine();
                sb.Append(path);

                var range = match.GetDocumentRange();
                if (range.IsValid())
                {
                    var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                    sb.Append(" :").Append(line).Append(':').Append(col);
                }

                var text = element.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    var snippet = text.Length > 150 ? text.Substring(0, 150) + "..." : text;
                    sb.Append(" - ").Append(snippet.Replace("\r\n", " ").Replace('\n', ' '));
                }

                shown++;
            }

            return sb.ToString().TrimEnd();
        }

        private string ExecuteReplace(string pattern, string replacement, string filePath, bool dryRun)
        {
            var (ssrPattern, searchDomain, error) = BuildRequest(pattern, filePath);
            if (error != null) return error;

            ssrPattern.ReplacePattern = replacement;
            ssrPattern.FormatAfterReplace = true;
            ssrPattern.ShortenReferences = true;

            IList<IStructuralMatchResult> matches;
            try
            {
                matches = SearchReplaceTargetsSequential(ssrPattern, searchDomain);
            }
            catch (Exception ex)
            {
                return $"structural_search replace failed - the pattern likely couldn't be parsed: {ex.Message}";
            }

            if (matches == null)
                return "Search did not complete (cancelled, or the pattern was rejected before matching started).";

            if (matches.Count == 0)
                return $"No matches found for pattern: {pattern} (nothing to replace)";

            var psiServices = _solution.GetPsiServices();
            using (var transaction = dryRun
                ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.structural_search.replace")
                : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.structural_search.replace"))
            {
                try
                {
                    CSharpStructuralSearchReplacer.Replace(matches, replacement, ssrPattern.Placeholders, formatAfterReplace: true, shortenReferences: true);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return $"structural_search replace failed while applying: {ex.Message}";
                }

                if (dryRun)
                    transaction.Rollback();

                var sb = new StringBuilder();
                sb.Append(dryRun ? "[dry run] " : "").Append("structural_search replace: ").Append(matches.Count)
                  .Append(dryRun ? " match(es) would be replaced" : " match(es) replaced")
                  .Append(" - pattern: ").AppendLine(pattern);
                sb.Append("replacement: ").AppendLine(replacement);

                var filesTouched = new HashSet<string>();
                foreach (var match in matches)
                {
                    var path = match.MatchedElement?.GetSourceFile()?.GetLocation().FullPath;
                    if (!string.IsNullOrEmpty(path))
                        filesTouched.Add(path);
                }

                if (filesTouched.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append(dryRun ? "would touch " : "touched ").Append(filesTouched.Count).AppendLine(" file(s):");
                    foreach (var path in filesTouched)
                        sb.Append("  ").AppendLine(path);
                }

                return sb.ToString().TrimEnd();
            }
        }

        // HANG FIX (2026-07-12, see docs/DEVNOTES.md "structural_search hang - root cause and fix"):
        // this reimplements JetBrains.ReSharper.Feature.Services.StructuralSearch.Finding.
        // StructuralSearchRequest.SearchReplaceTargets()/DoSearch() rather than calling it directly.
        // Root-caused via decompilation: StructuralSearchRequest.DoSearch() always drives the domain
        // traversal through StructuralSearchDomainSearcher<TResult>, which derives from
        // JetBrains.ReSharper.Psi.Search.SearchDomainVisitorParallel - NOT the plain synchronous
        // SearchDomainVisitor. SearchDomainVisitorParallel.Run() opens a
        // JetBrains.ReSharper.Psi.Threading.PsiTaskBarrier.CreateRead(..., sync: false, takeReadLock:
        // true, ...) and enqueues one job per source file onto it, then blocks (via the barrier's
        // dispose/WaitAll) until every enqueued job completes. That's an async multi-threaded fan-out
        // designed to run from an interactive, message-pumped context (the real VS/Rider UI thread) so
        // the worker jobs can actually get scheduled and each independently re-acquire a nested read
        // lock. Called from this plugin's headless dispatch thread (PsiThreadDispatcher.ExecuteRead,
        // itself already holding the one read lock via IShellLocks.ExecuteOrQueueReadLock, with no
        // message pump running underneath it), the enqueued per-file jobs never get a chance to run and
        // the outer wait blocks forever - the same "interactive-only SDK path called headlessly wedges
        // the PSI lock" failure class already hit and fixed for apply_quick_fix, just via a different
        // SDK entry point this time (parallel task-barrier fan-out instead of a live-template hotspot
        // session).
        //
        // The fix does not touch locking/threading at all - it avoids the parallel fan-out entirely.
        // SearchDomainVisitor (the non-Parallel base class SearchDomainVisitorParallel itself derives
        // from) is a plain, synchronous, single-threaded visitor: ISearchDomain.Accept(visitor) just
        // walks modules/files/elements via ordinary recursive method calls on the calling thread, no
        // task barrier, no extra locking. SequentialStructuralSearchVisitor below is a from-scratch
        // subclass of that plain base, calling the exact same StructuralSearcher.ProcessProjectItem/
        // ProcessElement methods StructuralSearchDomainSearcher<TResult> calls internally (both public,
        // confirmed via decompilation) - so matching semantics are identical, only the traversal
        // mechanism changes from async-fan-out to synchronous-inline. NarrowSearchDomain below is a
        // verbatim port of StructuralSearchRequest's own private word-index narrowing step (kept for
        // parity/performance on large solutions), using only public IWordIndex/SearchDomainFactory APIs.
        [CanBeNull]
        private IList<IStructuralMatchResult> SearchReplaceTargetsSequential(IStructuralSearchPattern ssrPattern, ISearchDomain searchDomain)
        {
            var structuralMatcher = ssrPattern.CreateMatcher();
            if (structuralMatcher == null)
                return null;

            var searcher = new StructuralSearcher(_documentManager, ssrPattern.Language, structuralMatcher);
            var narrowedDomain = NarrowSearchDomain(searchDomain, structuralMatcher.Words, structuralMatcher.GetExtendedWords(_solution));

            var results = new List<IStructuralMatchResult>();
            var consumer = new FindResultConsumer<IStructuralMatchResult>(
                result => (result is FindResultStructural { DocumentRange: var documentRange } findResultStructural && documentRange.IsValid())
                    ? findResultStructural.MatchResult
                    : null,
                match =>
                {
                    if (match != null) results.Add(match);
                    return FindExecution.Continue;
                });

            var visitor = new SequentialStructuralSearchVisitor(searcher, consumer);
            narrowedDomain.Accept(visitor);
            return results;
        }

        // Verbatim port of StructuralSearchRequest's private NarrowSearchDomain - see doc comment above.
        private ISearchDomain NarrowSearchDomain(ISearchDomain domain, IReadOnlyCollection<string> words, IReadOnlyCollection<string> extendedWords)
        {
            if (domain.IsEmpty || words.Count == 0)
                return domain;

            var wordIndex = _solution.GetPsiServices().WordIndex;
            IEnumerable<IPsiSourceFile> files = wordIndex.GetFilesContainingAllWords(words);

            if (extendedWords.Count > 0)
            {
                var narrowed = new HashSet<IPsiSourceFile>();
                foreach (var file in files)
                {
                    foreach (var extendedWord in extendedWords)
                    {
                        if (wordIndex.CanContainAllSubwords(file, extendedWord))
                        {
                            narrowed.Add(file);
                            break;
                        }
                    }
                }
                files = narrowed;
            }

            var searchDomainFactory = _solution.GetPsiServices().SearchDomainFactory;
            return domain.Intersect(searchDomainFactory.CreateSearchDomain(files));
        }

        // Plain, synchronous SearchDomainVisitor subclass - see the hang-fix doc comment above for why
        // this exists instead of using the SDK's own StructuralSearchDomainSearcher<TResult>.
        private sealed class SequentialStructuralSearchVisitor : SearchDomainVisitor
        {
            private readonly IStructuralSearcher _searcher;
            private readonly IFindResultConsumer<IStructuralMatchResult> _consumer;
            private bool _finished;

            public SequentialStructuralSearchVisitor(IStructuralSearcher searcher, IFindResultConsumer<IStructuralMatchResult> consumer)
            {
                _searcher = searcher;
                _consumer = consumer;
            }

            public override bool ProcessingIsFinished => _finished;

            public override void VisitPsiSourceFile(IPsiSourceFile sourceFile)
            {
                if (_finished) return;
                if (_searcher.ProcessProjectItem(sourceFile, _consumer))
                    _finished = true;
            }

            public override void VisitElement(ITreeNode element)
            {
                if (_finished) return;
                if (_searcher.ProcessElement(element, _consumer))
                    _finished = true;
            }
        }
    }
}
