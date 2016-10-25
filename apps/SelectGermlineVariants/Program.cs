﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExpressionLib;
using System.IO;
using System.Threading;
using System.Diagnostics;


namespace SelectGermlineVariants
{
    class Program
    {

        const long granularity = 1000; // How often to select a variant if one is available.
        const long isolationDistance = 150; // How many bases around a selected variant must match the germline exclusively.

        class CandidateVariant
        {
            public CandidateVariant(string line_, long locus_, double odds_, string chromosome_)
            {
                line = line_;
                locus = locus_;
                odds = odds_;
                chromosome = chromosome_;
            }

            public string chromosome;
            public string line;
            public long locus;
            public double odds;
            public int nRNAReads = -1;
            public int nDNAReads = -1;
        }

        static void EmitBestCandidate(StreamWriter outputFile, string chromosome, List<CandidateVariant> liveCandidates)
        {
            if (liveCandidates.Count() == 0)
            {
                return;
            }

            CandidateVariant bestCandiate = liveCandidates[0];

            for (int i = 0; i < liveCandidates.Count(); i++) // Yes, I know there's some clever c# way to do this with linq, but it would take me longer to look up than to write this loop
            {
                if (liveCandidates[i].odds > bestCandiate.odds)
                {
                    bestCandiate = liveCandidates[i];
                }
            }

            outputFile.WriteLine(chromosome + "\t" + bestCandiate.locus + "\t" + bestCandiate.line);
        }

        static void EliminateTooNearCandidates(List<CandidateVariant> liveCandidates, long locus)
        {
            var candidatesToEliminate = new List<CandidateVariant>();
            foreach (var candidate in liveCandidates)
            {
                if (candidate.locus + isolationDistance >= locus)
                {
                    candidatesToEliminate.Add(candidate);
                }
            }

            foreach (var candidate in candidatesToEliminate)
            {
                liveCandidates.Remove(candidate);
            }
        }


        static void markReadCount(Dictionary<string, Dictionary<long, CandidateVariant>> viableCandidates, bool dna, string contigName, long locus, int mappedReadCount)
        {
            contigName = contigName.ToLower();

            if (!viableCandidates.ContainsKey(contigName))
            {
                contigName = ExpressionTools.switchChrState(contigName);

                if (!viableCandidates.ContainsKey(contigName))
                {
                    //
                    // Probably a minor contig that doesn't have a high quality SNV called for it.  Just ignore it.
                    //
                    return;
                }
            }

            if (viableCandidates[contigName].ContainsKey(locus))
            {
                if (dna && viableCandidates[contigName][locus].nDNAReads != -1 || !dna && viableCandidates[contigName][locus].nRNAReads != -1)
                {
                    Console.WriteLine("Got read count more than once for same variant " + contigName + ":" + locus);
                    throw new FormatException();
                }

                if (dna)
                {
                    viableCandidates[contigName][locus].nDNAReads = mappedReadCount;
                }
                else
                {
                    viableCandidates[contigName][locus].nRNAReads = mappedReadCount;
                }
            }
        }

        static void ProcessRuns(List<FileSet> workItems)
        {
            bool firstRun = true;
            int nVariantsSelected = 0;
            while (true)
            {
                var timer = new Stopwatch();

                FileSet fileSet;
                lock (workItems)
                {
                    if (workItems.Count() == 0)
                    {
                        return;
                    }

                    fileSet = workItems[0];
                    workItems.RemoveAt(0);

                    if (!firstRun)
                    {
                        timer.Stop(); 
                        Console.Write("Found " + nVariantsSelected + " variants in " + ((timer.ElapsedMilliseconds + 500) / 1000) + "s;\t");
                        timer.Reset();
                        nVariantsSelected = 0;
                    }

                    Console.WriteLine("" + workItems.Count() + " remain" + (workItems.Count() == 1 ? "s" : "") + " queued.");
                }

                firstRun = false;
                timer.Start();

                StreamReader vcfFile = null;
                try
                {
                    vcfFile = new StreamReader(fileSet.vcfPathname);
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("File not found on vcf " + fileSet.vcfPathname + ".  Skipping.");
                    continue;
                }

                string line;
                while (null != (line = vcfFile.ReadLine()) && line.Count() != 0 && line[0] == '#') {
                    // Skip the header lines
                }

                if (null == line || line.Count() == 0) {
                    Console.WriteLine("Corrupt vcf: missing body: " + fileSet.vcfPathname);
                    continue;
                }

                //
                // First, read in all of the variants, saving those that we can't immediately exclude because of one reason or another.
                //

                string currentChromosome = "";

                bool badFile = false;

                var liveCandidates = new List<CandidateVariant>();
                var previousGrainsCandidates = new List<CandidateVariant>();
                var savedGrains = new List<List<CandidateVariant>>();               // Grains and all the candidate variants in them

                long lastLocus = -isolationDistance - 1;

                while (null != (line = vcfFile.ReadLine()))
                {
                    var fields = line.Split('\t');

                    if (fields.Count() != 10)
                    {
                        Console.WriteLine("Wrong number of fields (" + fields.Count() + " != 10) in vcf line: '" + line + "' in file " + fileSet.vcfPathname + ".  Ignoring file.");

                        badFile = true;
                        break;
                    }

                    var infoFields = fields[7].Split(';');

                    var info = new Dictionary<string, string>();
                    foreach (var infoField in infoFields)
                    {
                        var keyValue = infoField.Split('=');
                        if (keyValue.Count() != 2)
                        {
                            Console.WriteLine("Unable to parse info field '" + infoField + " in file " + fileSet.vcfPathname);
                            badFile = true;
                            break;
                        }

                        info.Add(keyValue[0], keyValue[1]);
                    }

                    if (!info.ContainsKey("AN") || !info.ContainsKey("AC") || !info.ContainsKey("CIGAR") || !info.ContainsKey("DP") || !info.ContainsKey("AF") || !info.ContainsKey("AB") || !info.ContainsKey("ODDS"))
                    {
                        Console.WriteLine("vcf line '" + line + " doesn't contain one or more required info fields.  Skipping file " + fileSet.vcfPathname);
                        badFile = true;
                    }

                    if (badFile)
                    {
                        break;
                    }

                    bool goodCandidate = true;
                    double alleleFrequency = 0;
                    double alleleBalance = 0;
                    int alleleCount = 0;
                    int alleleNumber = 0;
                    double odds = 0;
                    string cigar = info["CIGAR"];
                    long locus = 0;

                    try
                    {
                        if (info["AF"].Contains(',')) // This happens with multiple alleles (typically, heterozygous with both different from the reference).  Don't try to parse them, just don't select it
                        {
                            goodCandidate = false;
                        }
                        else
                        {
                            alleleFrequency = Convert.ToDouble(info["AF"]);
                            alleleBalance = Convert.ToDouble(info["AB"]);
                            alleleCount = Convert.ToInt32(info["AC"]);
                            alleleNumber = Convert.ToInt32(info["AN"]);
                            odds = Convert.ToDouble(info["ODDS"]);
                        }
                        locus = Convert.ToInt64(fields[1]);

                    }
                    catch (FormatException)
                    {
                        Console.WriteLine("Error parsing info fields in line " + line + " of file " + fileSet.vcfPathname + ".  Skipping file.");
                        badFile = true;
                        break;
                    }

                    if (fields[0] == currentChromosome && locus < lastLocus)
                    {
                        Console.WriteLine("out-of-order variant " + line + " in file " + fileSet.vcfPathname + ". Skipping file.");
                        badFile = true;
                        break;
                    }

                    //
                    // Figure out if we've moved into another grain, in which case we save the candidates we have from previous grains
                    //
                    if (currentChromosome != fields[0] || locus / granularity != lastLocus / granularity)
                    {
                        savedGrains.Add(previousGrainsCandidates);
                        previousGrainsCandidates = liveCandidates;
                        liveCandidates = new List<CandidateVariant>();

                        if (currentChromosome != fields[0])
                        {
                            //
                            // Starting a new chromosome, so we don't need to hang on to an old
                            // chromosome's candidates to make sure that we don't have any variants too close to
                            // the end of the old grain.
                            //
                            savedGrains.Add(previousGrainsCandidates);
                            previousGrainsCandidates = new List<CandidateVariant>();
                            currentChromosome = fields[0];
                            lastLocus = -isolationDistance - 1;
                        }
                    }

                    EliminateTooNearCandidates(liveCandidates, locus);
                    EliminateTooNearCandidates(previousGrainsCandidates, locus);

                    goodCandidate = goodCandidate && alleleFrequency == 0.5 && alleleBalance > 0.4 && alleleBalance < 0.6 && alleleCount == 1 && alleleNumber == 2 && cigar == "1X" && odds > 20 && lastLocus + isolationDistance < locus;

                    if (goodCandidate)
                    {
                        liveCandidates.Add(new CandidateVariant(line, locus, odds, currentChromosome.ToLower()));
                    }

                    lastLocus = locus;
                } // While we have a VCF line



                if (badFile) {
                    continue;
                }
                else
                {
                    //
                    // We now have a list of grains.  Make a map of the candidates in those grains that we can use to add in the DNA/RNA read count.
                    //

                    var viableCandidates = new Dictionary<string, Dictionary<long, CandidateVariant>>(); // Maps chromosome -> (locus -> candidate)

                    foreach (var grain in savedGrains)
                    {
                        foreach (var candidateVariant in grain)
                        {
                            if (!viableCandidates.ContainsKey(candidateVariant.chromosome))
                            {
                                viableCandidates.Add(candidateVariant.chromosome, new Dictionary<long, CandidateVariant>());
                            }

                            viableCandidates[candidateVariant.chromosome].Add(candidateVariant.locus, candidateVariant);
                        }
                    }

                    //
                    // Now read in the allcount files and use them to annotate the candidates.
                    //


                    ExpressionTools.AllcountReader.ProcessBase processDNABase = (contigName, locus, mappedReadCount) => markReadCount(viableCandidates, true, contigName, locus, mappedReadCount);
                    var dnaAllcountReader = new ExpressionTools.AllcountReader(fileSet.dnaAllcountPathmame);
                    long mappedHQNUclearReads;
                    int numContigs;
                    if (!dnaAllcountReader.openFile(out mappedHQNUclearReads, out numContigs))
                    {
                        Console.WriteLine("Couldn't open or bad header format in " + fileSet.dnaAllcountPathmame);
                        break;
                    }

                    if (!dnaAllcountReader.ReadAllcountFile(processDNABase))
                    {
                        Console.WriteLine("Bad internal format or truncation in " + fileSet.dnaAllcountPathmame);
                    }

                    ExpressionTools.AllcountReader.ProcessBase processRNABase = (contigName, locus, mappedReadCount) => markReadCount(viableCandidates, false, contigName, locus, mappedReadCount);
                    var rnaAllcountReader = new ExpressionTools.AllcountReader(fileSet.rnaAllcountPathmame);
                    if (!rnaAllcountReader.openFile(out mappedHQNUclearReads, out numContigs)) {
                        Console.WriteLine("Couldn't open or bad header format in " + fileSet.rnaAllcountPathmame);
                        break;
                    }
                    
                    if (!rnaAllcountReader.ReadAllcountFile(processRNABase)) {
                        Console.WriteLine("Bad internal format or truncation in " + fileSet.rnaAllcountPathmame);
                    }

                    //
                    // Now run through the grains, select only the variants that have enough reads, and emit the best one for each grain.
                    //
                    var outputFilename = fileSet.vcfPathname.Substring(0, fileSet.vcfPathname.LastIndexOf('.')) + ".selectedVariants";
                    var outputFile = new StreamWriter(outputFilename);
                    outputFile.WriteLine("SelectGermlineVariants v1.1 for input file " + fileSet.vcfPathname);      // v1.0 didn't take into account the read counts when selecting variants.

                    foreach (var grain in savedGrains)
                    {
                        var remainingCandidates = new List<CandidateVariant>();

                        foreach (var candidate in grain)
                        {
                            if (candidate.nDNAReads >= 10 && candidate.nRNAReads >= 10)
                            {
                                remainingCandidates.Add(candidate);
                            }
                        }

                        if (remainingCandidates.Count() > 0)
                        {
                            nVariantsSelected++;
                            EmitBestCandidate(outputFile, remainingCandidates[0].chromosome, remainingCandidates);
                        }
                    }

                    outputFile.WriteLine("**done**");
                    outputFile.Close();
                }

                vcfFile.Close();
            }
        }


        class FileSet
        {
            public FileSet(string participantId_, string vcfPathname_, string dnaAllcountPathname_, string rnaAllcountPathname_)
            {
                participantId = participantId_;
                vcfPathname = vcfPathname_;
                dnaAllcountPathmame = dnaAllcountPathname_;
                rnaAllcountPathmame = rnaAllcountPathname_;
            }

            public readonly string participantId;
            public readonly string vcfPathname;
            public readonly string dnaAllcountPathmame;
            public readonly string rnaAllcountPathmame;
        }
        static void Main(string[] args)
        {
            if (args.Count() == 0)
            {
                Console.WriteLine("usage: SelectGermlineVariants <participant ID>");
                return;
            }

            var filesetByParticipantId = new Dictionary<string, FileSet>();
                
            var workItems = new List<FileSet>();

            var experimentsFile = new StreamReader(@"\\gcr\scratch\b99\bolosky\experiments.txt");
            string line;

            experimentsFile.ReadLine(); // Skip the header

            while (null != (line = experimentsFile.ReadLine()))
            {
                var fields = line.Split('\t');

                filesetByParticipantId.Add(fields[ExpressionTools.ParticipantIDFieldNumber], new
                    FileSet(fields[ExpressionTools.ParticipantIDFieldNumber], fields[ExpressionTools.VCFPathnameFieldNumber], fields[ExpressionTools.TumorDNAAllcountFileFieldNumber], fields[ExpressionTools.TumorRNAAllcountFileFieldNumber]));
            }
            
            experimentsFile.Close();
            experimentsFile = null;

            int nValidParticipants = 0;

            foreach (var arg in args)
            {
                if (!filesetByParticipantId.ContainsKey(arg))
                {
                    Console.WriteLine(arg + " does not appear to be a participant ID; ignoring.");
                    continue;
                }

                if (filesetByParticipantId[arg].vcfPathname == "" || filesetByParticipantId[arg].dnaAllcountPathmame == "" || filesetByParticipantId[arg].rnaAllcountPathmame == "")
                {
                    Console.WriteLine(arg + " doesn't appear to have a cmoplete set of vcf and allcount files yet.  Ignoring.");
                    continue;
                }

                workItems.Add(filesetByParticipantId[arg]);
                nValidParticipants++;
            }


            var timer = new Stopwatch();
            timer.Start();

            var threads = new List<Thread>();
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                threads.Add(new Thread(() => ProcessRuns(workItems)));
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            timer.Stop();
            Console.WriteLine("Processed " + nValidParticipants + " participants in " + (timer.ElapsedMilliseconds + 500) / 1000 + "s");
        }
    }
}
