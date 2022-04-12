using LiveSplit.Model;
using System;
using System.Collections.Generic;
using System.Drawing;
using CrashTWoCLoadDetector;
using System.IO;
using Tesseract;
using System.Linq;
//using System.Threading;

namespace LiveSplit.UI.Components
{
    class CrashTWoCLoadRemovalComponent : IComponent
    {
        public string ComponentName
        {
            get { return "Crash TWoC Load Removal"; }
        }
        public GraphicsCache Cache { get; set; }


        public float PaddingBottom { get { return 0; } }
        public float PaddingTop { get { return 0; } }
        public float PaddingLeft { get { return 0; } }
        public float PaddingRight { get { return 0; } }

        public bool Refresh { get; set; }

        public IDictionary<string, Action> ContextMenuControls { get; protected set; }

        public CrashTWoCLoadRemovalSettings settings { get; set; }

        private bool isLoading = false;
        private bool waitOnFadeIn = false;
        private bool waitOnLoad = false;
        private bool isBlackScreen = false;
        private bool wasBlackScreen = false;
        private bool isFadeIn = false;
        private bool testSaved = false;
        private bool specialLoad = false;
        private readonly int WAIT_ON_LOAD_FRAMES = 300;
        private int waitFrames = 0;
        private int lowestBit = 0;
        private bool isCmpFinished = false;
        private string expectedResultEng = "LOADING";
        private string expectedResultJpn = "リ~ド⑤④ぅヽトー";
        private string expectedResultKrn = "로드숭중";

        private TimerModel timer;
        private bool timerStarted = false;
        FileStream log_file_stream = null;
        StreamWriter log_file_writer = null;

        public enum CrashTWoCState
        {
            RUNNING,
            LOADING
        }

        private CrashTWoCState TWoCState = CrashTWoCState.RUNNING;
        private int runningFrames = 0;
        private int pausedFrames = 0;
        private int pausedFramesSegment = 0;
        private string GameName = "";
        private string GameCategory = "";
        private int NumberOfSplits = 0;
        private List<string> SplitNames;
        private DateTime timerStart;
        private DateTime lastTime;
        private DateTime transitionStart;

        private DateTime segmentTimeStart;
        private LiveSplitState liveSplitState;
        //private Thread captureThread;
        private int framesSinceLastManualSplit = 0;
        private bool LastSplitSkip = false;
        private string curLanguage;
        private string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Components/tessdata");
        TesseractEngine engine;
        //TesseractEngine engine = new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Components/tessdata"), "eng", EngineMode.Default);

        //private HighResolutionTimer.HighResolutionTimer highResTimer;
        private List<int> NumberOfLoadsPerSplit;
        public CrashTWoCLoadRemovalComponent(LiveSplitState state)
        {

            GameName = state.Run.GameName;
            GameCategory = state.Run.CategoryName;
            NumberOfSplits = state.Run.Count;
            SplitNames = new List<string>();

            foreach (var split in state.Run)
            {
                SplitNames.Add(split.Name);
            }

            liveSplitState = state;
            NumberOfLoadsPerSplit = new List<int>();
            InitNumberOfLoadsFromState();
            settings = new CrashTWoCLoadRemovalSettings(state);
            InitTesseract();
            lastTime = DateTime.Now;
            segmentTimeStart = DateTime.Now;
            timer = new TimerModel { CurrentState = state };
            timer.CurrentState.OnStart += timer_OnStart;
            timer.CurrentState.OnReset += timer_OnReset;
            timer.CurrentState.OnSkipSplit += timer_OnSkipSplit;
            timer.CurrentState.OnSplit += timer_OnSplit;
            timer.CurrentState.OnUndoSplit += timer_OnUndoSplit;
            timer.CurrentState.OnPause += timer_OnPause;
            timer.CurrentState.OnResume += timer_OnResume;
            //highResTimer = new HighResolutionTimer.HighResolutionTimer(16.0f);
            //highResTimer.Elapsed += (s, e) => { CaptureLoads(); };
        }

        private void InitTesseract()
        {
            curLanguage = settings.platform;
            if (settings.platform == "ENG/PS2" || settings.platform == "ENG/XBOX&GC")
            {
                engine = new TesseractEngine(path, "eng", EngineMode.Default);
            }
            else if (settings.platform == "JPN/PS2")
            {
                engine = new TesseractEngine(path, "jpn", EngineMode.Default);
            }
            else
            {
                engine = new TesseractEngine(path, "kor", EngineMode.Default);
            }
        }

        private void timer_OnResume(object sender, EventArgs e)
        {
            timerStarted = true;
        }

        private void timer_OnPause(object sender, EventArgs e)
        {
            timerStarted = false;
        }

        private void InitNumberOfLoadsFromState()
        {
            NumberOfLoadsPerSplit = new List<int>();
            NumberOfLoadsPerSplit.Clear();

            if (liveSplitState == null)
            {
                return;
            }

            foreach (var split in liveSplitState.Run)
            {
                NumberOfLoadsPerSplit.Add(0);
            }

            //Quicker way to prevent OOB on last split as I'm not sure if the index will go over if the run finishes
            NumberOfLoadsPerSplit.Add(99999);
        }

        private int CumulativeNumberOfLoadsForSplitIndex(int splitIndex)
        {
            int numberOfLoads = 0;

            for (int idx = 0; (idx < NumberOfLoadsPerSplit.Count && idx <= splitIndex); idx++)
            {
                numberOfLoads += NumberOfLoadsPerSplit[idx];
            }
            return numberOfLoads;
        }

        private void CaptureLoads()
        {
            try
            {
                bool isLoad;

                if (timerStarted && !settings.isCalibratingBlacklevel)
                {
                    if (settings.platform == "ENG/PS2" || settings.platform == "JPN/PS2" || settings.platform == "KRN/PS2")
                    {
                        isLoad = CaptureLoadsPS2();
                    }
                    else
                    {
                        isLoad = CaptureLoadsXBOX();
                    }



                    if (settings.AutoSplitterEnabled && !(settings.AutoSplitterDisableOnSkipUntilSplit && LastSplitSkip))
                    {
                        if (isLoad && framesSinceLastManualSplit >= settings.AutoSplitterManualSplitDelayFrames)
                        {
                            NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex]++;

                            if (CumulativeNumberOfLoadsForSplitIndex(liveSplitState.CurrentSplitIndex) >= settings.GetCumulativeNumberOfLoadsForSplit(liveSplitState.CurrentSplit.Name))
                            {

                                timer.Split();


                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                isLoading = false;
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private bool CaptureLoadsXBOX()
        {
            bool isLoad = false;
            if ((timerStart.Ticks - liveSplitState.Run.Offset.Ticks) <= DateTime.Now.Ticks)
            {
                framesSinceLastManualSplit++;
                //Console.WriteLine("TIME NOW: {0}", DateTime.Now - lastTime);
                //Console.WriteLine("TIME DIFF START: {0}", DateTime.Now - lastTime);
                //lastTime = DateTime.Now;

                wasBlackScreen = isBlackScreen;
                //Capture image using the settings defined for the component
                Bitmap capture = settings.CaptureImage();
                isBlackScreen = FeatureDetector.IsBlackScreen(ref capture, settings.blacklevel);
                BitmapToPixConverter btp = new BitmapToPixConverter();
                //if (!testSaved)
                //{
                //    Bitmap jpntestbmp = new Bitmap("cutout.bmp");
                //    FeatureDetector.ClearBackgroundPostLoad(ref jpntestbmp);
                //    jpntestbmp.Save("jpntest.bmp");
                //    Pix jpntest = btp.Convert(jpntestbmp);
                //    using (var page = engine.Process(jpntest, PageSegMode.SingleChar))
                //    {
                //        Console.WriteLine(page.GetText());
                //    }
                //    testSaved = true;
                //}

                if (wasBlackScreen && !isBlackScreen)
                {
                    //This could be a pre-load transition, start timing it
                    transitionStart = DateTime.Now;
                    waitOnLoad = true;
                }

                if (!isBlackScreen && !waitOnFadeIn && waitOnLoad)
                {
                    specialLoad = FeatureDetector.ClearBackground(ref capture, settings.blacklevel);
                    Pix img = btp.Convert(capture);
                    string ResultText = "";
                    using (var page = engine.Process(img, PageSegMode.SingleChar))
                    {
                        ResultText = page.GetText();
                    }
                    int counter = 0;
                    foreach (char c in expectedResultEng)
                    {
                        if (ResultText.Contains(c))
                            counter++;
                    }
                    if (counter > 5 && ResultText.Length == 8)
                    {
                        isLoading = true;
                        isLoad = true;
                    }

                }

                timer.CurrentState.IsGameTimePaused = isLoading || isBlackScreen;

                if (waitOnFadeIn && !specialLoad)
                {
                    capture = settings.CaptureImagePostLoad();
                    int lowestBitLast = lowestBit;
                    lowestBit = FeatureDetector.ClearBackgroundPostLoad(ref capture, settings.blacklevel);
                    if (lowestBit == lowestBitLast && lowestBit != 0)
                    {
                        Pix img = btp.Convert(capture);
                        using (var page = engine.Process(img, PageSegMode.SingleChar))
                        {
                            string result = page.GetText();
                            if (result != "\n")
                            {
                                Console.WriteLine(page.GetText());
                                waitOnFadeIn = false;
                                isLoading = false;
                                lowestBit = 0;
                                //the lifecounter coming in from the top takes a quarter of a second to stop
                                TimeSpan quarter = new TimeSpan(2500000);
                                timer.CurrentState.SetGameTime(timer.CurrentState.GameTimePauseTime + quarter);
                            }
                        }
                    }
                }

                if (waitOnFadeIn && isBlackScreen && !specialLoad)
                {
                    waitOnFadeIn = false;
                    isLoading = false;
                }

                if (waitOnFadeIn && specialLoad && FeatureDetector.IsEndOfSpecialLoad(ref capture, settings.blacklevel))
                {
                    specialLoad = false;
                    waitOnFadeIn = false;
                    isLoading = false;
                }

                if (isLoading && waitOnLoad)
                {
                    // This was a pre-load transition, subtract the gametime
                    TimeSpan delta = (DateTime.Now - transitionStart);
                    timer.CurrentState.SetGameTime(timer.CurrentState.GameTimePauseTime - delta);
                    waitOnLoad = false;
                    waitOnFadeIn = true;
                }
            }
            return isLoad;
        }

        private bool CaptureLoadsPS2()
        {
            bool isLoad = false;
            framesSinceLastManualSplit++;
            //Console.WriteLine("TIME NOW: {0}", DateTime.Now - lastTime);13
            //Console.WriteLine("TIME DIFF START: {0}", DateTime.Now - lastTime);
            lastTime = DateTime.Now;

            //Capture image using the settings defined for the component
            Bitmap capture = settings.CaptureImage();
            wasBlackScreen = isBlackScreen;
            isBlackScreen = FeatureDetector.IsBlackScreen(ref capture, settings.blacklevel);
            //BitmapToPixConverter btp = new BitmapToPixConverter();
            //if (!testSaved)
            //{
            //    Bitmap jpntestbmp = new Bitmap("cutout.bmp");
            //    FeatureDetector.GetBlackLevel(ref jpntestbmp);
            //    FeatureDetector.ClearBackground(ref jpntestbmp);
            //    jpntestbmp.Save("jpntest.bmp");
            //    Pix jpntest = btp.Convert(jpntestbmp);
            //    using (var page = engine.Process(jpntest, PageSegMode.SingleChar))
            //    {
            //        Console.WriteLine(page.GetText());
            //    }
            //    testSaved = true;
            //}


            if (!isBlackScreen && waitOnLoad)
            {
                FeatureDetector.ClearBackground(ref capture, settings.blacklevel);

                BitmapToPixConverter btp = new BitmapToPixConverter();
                Pix img = btp.Convert(capture);
                string ResultText = "";
                using (var page = engine.Process(img, PageSegMode.SingleChar))
                {
                    ResultText = page.GetText();
                    //Console.WriteLine(ResultText);
                }
                int counter = 0;
                if (settings.platform == "ENG/PS2")
                {
                    foreach (char c in expectedResultEng)
                    {
                        if (ResultText.Contains(c))
                            counter++;
                    }
                    if (counter > 5 && ResultText.Length == 8)
                    {
                        isLoading = true;
                        isLoad = true;
                    }
                    else
                    {
                        waitFrames++;
                        if (waitFrames == WAIT_ON_LOAD_FRAMES)
                        {
                            waitOnLoad = false;
                            waitFrames = 0;
                        }
                    }
                }
                else if (settings.platform == "JPN/PS2")
                {
                    foreach (char c in expectedResultJpn)
                    {
                        if (ResultText.Contains(c))
                            counter++;
                    }
                    if (counter > 3)
                    {
                        isLoading = true;
                        isLoad = true;
                    }
                    else
                    {
                        waitFrames++;
                        if (waitFrames == WAIT_ON_LOAD_FRAMES)
                        {
                            waitOnLoad = false;
                            waitFrames = 0;
                        }
                    }
                }
                //TODO: add korean
                else
                {
                    foreach (char c in expectedResultKrn)
                    {
                        if (ResultText.Contains(c))
                            counter++;
                    }
                    if (counter > 1)
                    {
                        isLoading = true;
                        isLoad = true;
                    }
                    else
                    {
                        waitFrames++;
                        if (waitFrames == WAIT_ON_LOAD_FRAMES)
                        {
                            waitOnLoad = false;
                            waitFrames = 0;
                        }
                    }
                }
            }

            timer.CurrentState.IsGameTimePaused = isLoading || isBlackScreen;

            if (waitOnFadeIn && isBlackScreen)
            {
                waitOnFadeIn = false;
                isLoading = false;
            }


            if (wasBlackScreen && !isBlackScreen)
            {
                //This could be a pre-load transition, start timing it
                transitionStart = DateTime.Now;
                waitOnLoad = true;
                waitFrames = 0;
            }



            if (isLoading && waitOnLoad)
            {
                // This was a pre-load transition, subtract the gametime
                TimeSpan delta = (DateTime.Now - transitionStart);
                timer.CurrentState.SetGameTime(timer.CurrentState.GameTimePauseTime - delta);
                waitOnLoad = false;
                waitFrames = 0;
                waitOnFadeIn = true;
            }
            return isLoad;
        }

        private void CalibrateBlacklevel()
        {
            Bitmap capture = settings.CaptureImage();
            int tempBlacklevel = FeatureDetector.GetBlackLevel(ref capture);
            if (tempBlacklevel != -1 && tempBlacklevel < settings.cmpBlackLevel)
            {
                settings.cmpBlackLevel = tempBlacklevel;
            }
            FeatureDetector.ClearBackground(ref capture, settings.cmpBlackLevel);

            BitmapToPixConverter btp = new BitmapToPixConverter();
            Pix img = btp.Convert(capture);
            string ResultText = "";
            using (var page = engine.Process(img, PageSegMode.SingleChar))
            {
                ResultText = page.GetText();
            }
            int counter = 0;
            if (settings.platform == "ENG/PS2" || settings.platform == "ENG/XBOX&GC")
            {
                foreach (char c in expectedResultEng)
                {
                    if (ResultText.Contains(c))
                        counter++;
                }
                if (counter > 5 && ResultText.Length == 8)
                {
                    isCmpFinished = true;
                }
            }
            else if (settings.platform == "JPN/PS2")
            {
                foreach (char c in expectedResultJpn)
                {
                    if (ResultText.Contains(c))
                        counter++;
                }
                if (counter > 3)
                {
                    isCmpFinished = true;
                }
            }
            else
            {
                foreach (char c in expectedResultKrn)
                {
                    if (ResultText.Contains(c))
                        counter++;
                }
                if (counter > 2)
                {
                    isCmpFinished = true;
                }
            }
            if (isCmpFinished)
            {
                settings.blacklevel = settings.cmpBlackLevel;
                isCmpFinished = false;
                settings.isCalibratingBlacklevel = false;
                settings.cmpBlackLevel = 100;
                Console.WriteLine("BLACKLEVEL: {0}", settings.blacklevel);
                settings.UpdateBlacklevelLabel();
                settings.Refresh();
            }
        }

        private void timer_OnUndoSplit(object sender, EventArgs e)
        {
            //skippedPauses -= settings.GetAutoSplitNumberOfLoadsForSplit(liveSplitState.Run[liveSplitState.CurrentSplitIndex + 1].Name);
            runningFrames = 0;
            pausedFrames = 0;

            //If we undo a split that already has met the required number of loads, we probably want the number to reset.
            if (NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex] >= settings.GetAutoSplitNumberOfLoadsForSplit(liveSplitState.CurrentSplit.Name))
            {
                NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex] = 0;
            }

            //Otherwise - we're fine. If it is a split that was skipped earlier, we still keep track of how we're standing.


        }

        private void timer_OnSplit(object sender, EventArgs e)
        {
            runningFrames = 0;
            pausedFrames = 0;
            framesSinceLastManualSplit = 0;
            //If we split, we add all remaining loads to the last split.
            //This means that the autosplitter now starts at 0 loads on the next split.
            //This is just necessary for manual splits, as automatic splits will always have a difference of 0.
            var loadsRequiredTotal = settings.GetCumulativeNumberOfLoadsForSplit(liveSplitState.Run[liveSplitState.CurrentSplitIndex - 1].Name);
            var loadsCurrentTotal = CumulativeNumberOfLoadsForSplitIndex(liveSplitState.CurrentSplitIndex - 1);
            NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex - 1] += loadsRequiredTotal - loadsCurrentTotal;

            LastSplitSkip = false;
        }

        private void timer_OnSkipSplit(object sender, EventArgs e)
        {

            runningFrames = 0;
            pausedFrames = 0;

            //We don't need to do anything here - we just keep track of loads per split now.
            LastSplitSkip = true;

            /*if(settings.AutoSplitterDisableOnSkipUntilSplit)
                  {
                      NumberOfLoadsPerSplit[liveSplitState.CurrentSplitIndex - 1] = 0;
                  }*/
        }

        private void timer_OnReset(object sender, TimerPhase value)
        {
            timerStarted = false;
            runningFrames = 0;
            pausedFrames = 0;
            specialLoad = false;
            framesSinceLastManualSplit = 0;
            LastSplitSkip = false;
            isLoading = false;
            waitOnFadeIn = false;
            waitOnLoad = false;
            isBlackScreen = false;
            wasBlackScreen = false;
            waitFrames = 0;
            lowestBit = 0;
            testSaved = false;

            //highResTimer.Stop(joinThread:false);
            InitNumberOfLoadsFromState();

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }

        }

        void timer_OnStart(object sender, EventArgs e)
        {
            InitNumberOfLoadsFromState();
            timer.InitializeGameTime();
            runningFrames = 0;
            framesSinceLastManualSplit = 0;
            pausedFrames = 0;
            timerStarted = true;
            timerStart = DateTime.Now;

            ReloadLogFile();
            //StartCaptureThread();
            //highResTimer.Start();
        }

        private void ReloadLogFile()
        {
            if (settings.SaveDetectionLog == false)
                return;


            System.IO.Directory.CreateDirectory(settings.DetectionLogFolderName);

            string fileName = Path.Combine(settings.DetectionLogFolderName + "/", "CrashTWoCLoadRemoval_Log_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_") + settings.removeInvalidXMLCharacters(GameName) + "_" + settings.removeInvalidXMLCharacters(GameCategory) + ".txt");

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }


            log_file_stream = new FileStream(fileName, FileMode.Create);
            log_file_writer = new StreamWriter(log_file_stream);
            log_file_writer.AutoFlush = true;
            Console.SetOut(log_file_writer);
            Console.SetError(log_file_writer);

        }

        private bool SplitsAreDifferent(LiveSplitState newState)
        {
            //If GameName / Category is different
            if (GameName != newState.Run.GameName || GameCategory != newState.Run.CategoryName)
            {
                GameName = newState.Run.GameName;
                GameCategory = newState.Run.CategoryName;
                return true;
            }

            //If number of splits is different
            if (newState.Run.Count != liveSplitState.Run.Count)
            {
                NumberOfSplits = newState.Run.Count;
                return true;
            }

            //Check if any split name is different.
            for (int splitIdx = 0; splitIdx < newState.Run.Count; splitIdx++)
            {
                if (newState.Run[splitIdx].Name != SplitNames[splitIdx])
                {

                    SplitNames = new List<string>();

                    foreach (var split in newState.Run)
                    {
                        SplitNames.Add(split.Name);
                    }

                    return true;
                }

            }



            return false;
        }

        private bool LanguageIsDifferent()
        {
            return settings.platform != curLanguage;
        }
        public void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            if (SplitsAreDifferent(state))
            {
                settings.ChangeAutoSplitSettingsToGameName(GameName, GameCategory);

                ReloadLogFile();
            }
            if (LanguageIsDifferent())
            {
                InitTesseract();
            }
            liveSplitState = state;
            /*
                  liveSplitState = state;
                  if (GameName != state.Run.GameName || GameCategory != state.Run.CategoryName)
                  {
                      //Reload settings for different game or category
                      GameName = state.Run.GameName;
                      GameCategory = state.Run.CategoryName;

                      settings.ChangeAutoSplitSettingsToGameName(GameName, GameCategory);
                  }
                  */



            CaptureLoads();

            if (settings.isCalibratingBlacklevel)
            {
                CalibrateBlacklevel();
            }



        }

        public void DrawHorizontal(Graphics g, LiveSplitState state, float height, Region clipRegion)
        {

        }

        public void DrawVertical(Graphics g, LiveSplitState state, float width, Region clipRegion)
        {

        }

        public float VerticalHeight
        {
            get { return 0; }
        }

        public float MinimumWidth
        {
            get { return 0; }
        }

        public float HorizontalWidth
        {
            get { return 0; }
        }

        public float MinimumHeight
        {
            get { return 0; }
        }

        public System.Xml.XmlNode GetSettings(System.Xml.XmlDocument document)
        {
            return settings.GetSettings(document);
        }

        public System.Windows.Forms.Control GetSettingsControl(UI.LayoutMode mode)
        {
            return settings;
        }

        public void SetSettings(System.Xml.XmlNode settings)
        {
            this.settings.SetSettings(settings);
        }

        public void RenameComparison(string oldName, string newName)
        {
        }

        public void Dispose()
        {
            timer.CurrentState.OnStart -= timer_OnStart;

            if (log_file_writer != null)
            {
                if (log_file_writer.BaseStream != null)
                {
                    log_file_writer.Flush();
                    log_file_writer.Close();
                    log_file_writer.Dispose();
                }
                log_file_writer = null;
            }

        }
    }
}
