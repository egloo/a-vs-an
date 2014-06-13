﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using System.Xml.Linq;
using System.Xml;
using System.Diagnostics;

namespace WikipediaAvsAnTrieExtractor {
    static class Program {
        static int Main(string[] args) {
            if (args.Length != 2) {
                Console.Error.WriteLine("Usage: AvsAnTrie <wikidumpfile> <outputfile>\nThe dump is available at http://dumps.wikimedia.org/enwiki/latest/");
                return 1;
            }
            var wikiPath = args[0];
            if (!File.Exists(wikiPath)) {
                Console.Error.WriteLine("The wikipedia dump file could not be found at " + args[0]);
                return 1;
            }
            if (File.Exists(args[1])) {
                Console.Error.WriteLine("The output file " + args[1] + "already exists; delete it or pick another location.");
                return 1;
            }
            var outputFilePath = args[1];
            Task.Factory.StartNew(() => {

                while (true) {
                    Thread.Sleep(1000);
                    Console.WriteLine(string.Join("; ", ProgressReporters.Select(f => f())));
                }
            }, TaskCreationOptions.LongRunning);

            CreateAvsAnStatistics(wikiPath, outputFilePath);
            return 0;
        }

        static readonly List<Func<string>> ProgressReporters = new List<Func<string>>();
        const int PageBlocksQueueSize = 10;
        const int SightingBlocksQueueSize= 100;
        const int PagesPerBlock = 50;

        static void CreateAvsAnStatistics(string wikiPath, string outputFilePath) {
            var wikiPageQueue = LoadWikiPagesAsync(wikiPath);
            var entriesTodo = ExtractAvsAnSightingsAsync(wikiPageQueue);
            var trieBuilder = BuildAvsAnTrie(entriesTodo);
            AnnotatedTrie result = trieBuilder.Result;
            Console.WriteLine("Before simplification: trie of # nodes" + trieBuilder.Result.CountParallel);
            File.WriteAllText(outputFilePath + ".large", result.Readable());
            result.Simplify();
            File.WriteAllText(outputFilePath, result.Readable());
            Console.WriteLine("After simplification: trie of # nodes" + trieBuilder.Result.CountParallel);
        }

        static BlockingCollection<AvsAnSighting[]> ExtractAvsAnSightingsAsync(BlockingCollection<string[]> wikiPageQueue) {
            var entriesTodo = new BlockingCollection<AvsAnSighting[]>(SightingBlocksQueueSize);
            ProgressReporters.Add(() => "word queue: " + entriesTodo.Count);

            var sightingExtractionTask = Task.WhenAll(
                Enumerable.Range(0, Math.Max(2,Environment.ProcessorCount-1)).Select(i =>
                    Task.Factory.StartNew(() => {
                        var ms = new RegexTextUtils();
                        foreach (var pageSet in wikiPageQueue.GetConsumingEnumerable())
                            foreach (var page in pageSet)
                                entriesTodo.Add(ms.FindAvsAnSightings(page));
                    }, TaskCreationOptions.LongRunning)
                    ).ToArray()
                );

            sightingExtractionTask.ContinueWith(t => {
                if (t.IsFaulted)
                    Console.WriteLine(t.Exception);
                entriesTodo.CompleteAdding();
            });
            return entriesTodo;
        }

        static Task<AnnotatedTrie> BuildAvsAnTrie(BlockingCollection<AvsAnSighting[]> entriesTodo) {
            int wordCount = 0;

            Stopwatch sw = Stopwatch.StartNew();
            ProgressReporters.Add(() => "words/ms: " + (wordCount / sw.Elapsed.TotalMilliseconds).ToString("f1"));


            var trieBuilder = Task.Factory.StartNew(() => {
                var trie = new AnnotatedTrie();
                foreach (var entries in entriesTodo.GetConsumingEnumerable())
                    foreach (var entry in entries) {
                        trie.AddEntry(entry.PrecededByAn, entry.Word, 0);
                        wordCount++;
                    }
                return trie;
            }, TaskCreationOptions.LongRunning);
            return trieBuilder;
        }

        static BlockingCollection<string[]> LoadWikiPagesAsync(string wikiPath) {
            var wikiPageQueue = new BlockingCollection<string[]>(PageBlocksQueueSize);

            ProgressReporters.Add(() => "page queue: " + wikiPageQueue.Count);


            Task.Factory.StartNew(() => {
                var sw = Stopwatch.StartNew();
                using (var stream = File.OpenRead(wikiPath))
                using (var reader = XmlReader.Create(stream)) {
                    bool stopped = false;
                    try {
                        long pageCount = 0;
                        ProgressReporters.Add(() => stopped ? "" : "MB/s: " + (stream.Position / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds).ToString("f1"));
                        ProgressReporters.Add(() => stopped ? "" : "pages/ms: " + (pageCount / sw.Elapsed.TotalMilliseconds).ToString("f1"));
                        var pages = new string[1];
                        int i = 0;

                        while (reader.Read()) {
                            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "page") {
                                if (reader.ReadToDescendant("text") && reader.Read() && reader.Value!=null) {
                                    pageCount++;
                                    pages[i] = reader.Value;
                                    i++;
                                    if (i == pages.Length) {
                                        wikiPageQueue.Add(pages);
                                        pages = new string[Math.Min(pages.Length+1,PagesPerBlock)];
                                        i = 0;
                                    }
                                }
                            }
                        }
                        wikiPageQueue.Add(pages.Take(i).ToArray());
                    } finally {
                        stopped = true;
                    }
                }
                wikiPageQueue.CompleteAdding();
            }, TaskCreationOptions.LongRunning);
            return wikiPageQueue;
        }
    }
}
