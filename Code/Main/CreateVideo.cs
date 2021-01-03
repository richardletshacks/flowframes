﻿using Flowframes;
using Flowframes.FFmpeg;
using Flowframes.IO;
using Flowframes.Magick;
using Flowframes.Main;
using Flowframes.OS;
using Flowframes.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Padding = Flowframes.Data.Padding;
using i = Flowframes.Interpolate;
using System.Diagnostics;
using Flowframes.AudioVideo;

namespace Flowframes.Main
{
    class CreateVideo
    {
        static string currentOutFile;   // Keeps track of the out file, in case it gets renamed (FPS limiting, looping, etc) before finishing export

        public static async Task Export(string path, string outPath, i.OutMode mode)
        {
            if (!mode.ToString().ToLower().Contains("vid"))     // Copy interp frames out of temp folder and skip video export for image seq export
            {
                try
                {
                    await CopyOutputFrames(path, Path.GetFileNameWithoutExtension(outPath));
                }
                catch(Exception e)
                {
                    Logger.Log("Failed to move interp frames folder: " + e.Message);
                }
                return;
            }

            if (IOUtils.GetAmountOfFiles(path, false, $"*.{InterpolateUtils.GetOutExt()}") <= 1)
            {
                i.Cancel("Output folder does not contain frames - An error must have occured during interpolation!", AiProcess.hasShownError);
                return;
            }
            await Task.Delay(10);
            Program.mainForm.SetStatus("Creating output video from frames...");
            try
            {
                int maxFps = Config.GetInt("maxFps");
                if (maxFps == 0) maxFps = 500;

                if (i.current.outFps > maxFps)
                    await Encode(mode, path, outPath, i.current.outFps, maxFps, (Config.GetInt("maxFpsMode") == 1));
                else
                    await Encode(mode, path, outPath, i.current.outFps);
            }
            catch (Exception e)
            {
                Logger.Log("FramesToVideo Error: " + e.Message, false);
                MessageBox.Show("An error occured while trying to convert the interpolated frames to a video.\nCheck the log for details.");
            }
        }

        static async Task CopyOutputFrames (string framesPath, string folderName)
        {
            Program.mainForm.SetStatus("Copying output frames...");
            string copyPath = Path.Combine(i.current.outPath, folderName);
            Logger.Log($"Copying interpolated frames to '{copyPath}'");
            IOUtils.TryDeleteIfExists(copyPath);
            IOUtils.CreateDir(copyPath);
            Stopwatch sw = new Stopwatch();
            sw.Restart();

            string vfrFile = Path.Combine(framesPath.GetParentDir(), $"vfr-{i.current.interpFactor}x.ini");
            string[] vfrLines = IOUtils.ReadLines(vfrFile);

            for (int idx = 1; idx <= vfrLines.Length; idx++)
            {
                string line = vfrLines[idx-1];
                string inFilename = line.Split('/').Last().Remove("'");
                string framePath = Path.Combine(framesPath, inFilename);
                string outFilename = Path.Combine(copyPath, idx.ToString().PadLeft(Padding.interpFrames, '0')) + InterpolateUtils.GetOutExt(true);

                if ((idx < vfrLines.Length) && vfrLines[idx].Contains(inFilename))   // If file is re-used in the next line, copy instead of move
                    File.Copy(framePath, outFilename);
                else
                    File.Move(framePath, outFilename);

                if (sw.ElapsedMilliseconds >= 1000 || idx == vfrLines.Length)
                {
                    sw.Restart();
                    Logger.Log($"Copying interpolated frames to '{Path.GetFileName(copyPath)}' - {idx}/{vfrLines.Length}", false, true);
                    await Task.Delay(1);
                }
            }
        }

        static async Task Encode(i.OutMode mode, string framesPath, string outPath, float fps, float changeFps = -1, bool keepOriginalFpsVid = true)
        {
            currentOutFile = outPath;
            string vfrFile = Path.Combine(framesPath.GetParentDir(), $"vfr-{i.current.interpFactor}x.ini");

            if (mode == i.OutMode.VidGif)
            {
                await FFmpegCommands.FramesToGifVfr(vfrFile, outPath, true, Config.GetInt("gifColors"));
            }
            else
            {
                int looptimes = GetLoopTimes();
                bool h265 = Config.GetInt("mp4Enc") == 1;
                int crf = h265 ? Config.GetInt("h265Crf") : Config.GetInt("h264Crf");

                await FFmpegCommands.FramesToVideoConcat(vfrFile, outPath, mode, fps);
                await MergeAudio(i.current.inPath, outPath);

                if (changeFps > 0)
                {
                    currentOutFile = IOUtils.FilenameSuffix(outPath, $"-{changeFps.ToString("0")}fps");
                    Program.mainForm.SetStatus("Creating video with desired frame rate...");
                    await FFmpegCommands.ConvertFramerate(outPath, currentOutFile, h265, crf, changeFps, !keepOriginalFpsVid);
                    await MergeAudio(i.current.inPath, currentOutFile);
                }

                if (looptimes > 0)
                    await Loop(currentOutFile, looptimes);

            }
        }

        public static async Task ChunksToVideo(string chunksPath, string vfrFile, string outPath)
        {
            if (IOUtils.GetAmountOfFiles(chunksPath, false, $"*{FFmpegUtils.GetExt(i.current.outMode)}") < 1)
            {
                i.Cancel("No video chunks found - An error must have occured during chunk encoding!", AiProcess.hasShownError);
                return;
            }
            await Task.Delay(10);
            Program.mainForm.SetStatus("Merging video chunks...");
            try
            {
                int maxFps = Config.GetInt("maxFps");
                if (maxFps == 0) maxFps = 500;

                if (i.current.outFps > maxFps)
                    await MergeChunks(chunksPath, vfrFile, outPath, i.current.outFps, maxFps, (Config.GetInt("maxFpsMode") == 1));
                else
                    await MergeChunks(chunksPath, vfrFile, outPath, i.current.outFps);
            }
            catch (Exception e)
            {
                Logger.Log("ChunksToVideo Error: " + e.Message, false);
                MessageBox.Show("An error occured while trying to merge the video chunks.\nCheck the log for details.");
            }
        }

        static async Task MergeChunks(string chunksPath, string vfrFile, string outPath, float fps, float changeFps = -1, bool keepOriginalFpsVid = true)
        {
            int looptimes = GetLoopTimes();

            bool h265 = Config.GetInt("mp4Enc") == 1;
            int crf = h265 ? Config.GetInt("h265Crf") : Config.GetInt("h264Crf");

            await FFmpegCommands.ConcatVideos(vfrFile, outPath, fps, -1);
            await MergeAudio(i.current.inPath, outPath);

            if (looptimes > 0)
                await Loop(outPath, looptimes);

            if (changeFps > 0)
            {
                string newOutPath = IOUtils.FilenameSuffix(outPath, $"-{changeFps.ToString("0")}fps");
                Program.mainForm.SetStatus("Creating video with desired frame rate...");
                await FFmpegCommands.ConvertFramerate(outPath, newOutPath, h265, crf, changeFps, !keepOriginalFpsVid);
                await MergeAudio(i.current.inPath, newOutPath);
                if (looptimes > 0)
                    await Loop(newOutPath, looptimes);
            }
        }

        public static async Task EncodeChunk(string outPath, i.OutMode mode, int firstFrameNum, int framesAmount)
        {
            string vfrFileOriginal = Path.Combine(i.current.tempFolder, $"vfr-{i.current.interpFactor}x.ini");
            string vfrFile = Path.Combine(i.current.tempFolder, $"vfr-chunk-{firstFrameNum}-{firstFrameNum + framesAmount}.ini");
            File.WriteAllLines(vfrFile, IOUtils.ReadLines(vfrFileOriginal).Skip(firstFrameNum).Take(framesAmount));

            await FFmpegCommands.FramesToVideoConcat(vfrFile, outPath, mode, i.current.outFps, AvProcess.LogMode.Hidden, true);
        }

        static async Task Loop(string outPath, int looptimes)
        {
            Logger.Log($"Looping {looptimes} times to reach target length...");
            await FFmpegCommands.LoopVideo(outPath, looptimes, Config.GetInt("loopMode") == 0);
        }

        static int GetLoopTimes()
        {
            //Logger.Log("Getting loop times for path " + framesOutPath);
            int times = -1;
            int minLength = Config.GetInt("minOutVidLength");
            //Logger.Log("minLength: " + minLength);
            int minFrameCount = (minLength * i.current.outFps).RoundToInt();
            //Logger.Log("minFrameCount: " + minFrameCount);
            //int outFrames = new DirectoryInfo(framesOutPath).GetFiles($"*.{InterpolateUtils.GetExt()}", SearchOption.TopDirectoryOnly).Length;
            int outFrames = i.currentInputFrameCount * i.current.interpFactor;
            //Logger.Log("outFrames: " + outFrames);
            if (outFrames / i.current.outFps < minLength)
                times = (int)Math.Ceiling((double)minFrameCount / (double)outFrames);
            //Logger.Log("times: " + times);
            times--;    // Account for this calculation not counting the 1st play (0 loops)
            if (times <= 0) return -1;      // Never try to loop 0 times, idk what would happen, probably nothing
            return times;
        }

        public static async Task MergeAudio(string inputPath, string outVideo, int looptimes = -1)
        {
            if (!Config.GetBool("enableAudio")) return;
            try
            {
                string audioFileBasePath = Path.Combine(i.current.tempFolder, "audio");

                if (inputPath != null && IOUtils.IsPathDirectory(inputPath) && !File.Exists(IOUtils.GetAudioFile(audioFileBasePath)))   // Try loading out of same folder as input if input is a folder
                    audioFileBasePath = Path.Combine(i.current.tempFolder.GetParentDir(), "audio");

                if (!File.Exists(IOUtils.GetAudioFile(audioFileBasePath)))
                    await FFmpegCommands.ExtractAudio(inputPath, audioFileBasePath);      // Extract from sourceVideo to audioFile unless it already exists
                
                if (!File.Exists(IOUtils.GetAudioFile(audioFileBasePath)) || new FileInfo(IOUtils.GetAudioFile(audioFileBasePath)).Length < 4096)
                {
                    Logger.Log("No compatible audio stream found.", true);
                    return;
                }
                await FFmpegCommands.MergeAudio(outVideo, IOUtils.GetAudioFile(audioFileBasePath));        // Merge from audioFile into outVideo
            }
            catch (Exception e)
            {
                Logger.Log("Failed to copy audio!");
                Logger.Log("MergeAudio() Exception: " + e.Message, true);
            }
        }
    }
}
