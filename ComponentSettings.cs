using CrashTWoCLoadDetector;
using LiveSplit.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.UI.Components
{


    public partial class CrashTWoCLoadRemovalSettings : UserControl
    {
        #region Public Fields

        public string platform = "ENG/PS2";

        public bool AutoSplitterEnabled = false;

        public bool AutoSplitterDisableOnSkipUntilSplit = false;

        public bool isCalibratingBlacklevel = false;

        public int blacklevel = 100;

        public bool LoadRemoverEnabled { get { return !disableLoadRemover.Checked; } }
        public event EventHandler LoadRemoverEnabledChanged;
        public event EventHandler DetectionConfigurationChanged;

        public int cmpBlackLevel = 100;

        public bool RemoveFadeouts = false;
        public bool RemoveFadeins = false;

        public bool SaveDetectionLog = false;

        public int AverageBlackLevel = -1;

        public string DetectionLogFolderName = "CrashTWoCLoadRemovalLog";

        //Number of frames to wait for a change from load -> running and vice versa.
        public int AutoSplitterJitterToleranceFrames = 8;

        //If you split manually during "AutoSplitter" mode, I ignore AutoSplitter-splits for 50 frames. (A little less than 2 seconds)
        //This means that if a split would happen during these frames, it is ignored.
        public int AutoSplitterManualSplitDelayFrames = 50;

        #endregion Public Fields

        #region Private Fields

        private AutoSplitData autoSplitData = null;
        private readonly bool capturePreview;

        private float captureAspectRatioX = 16.0f;

        private float captureAspectRatioY = 9.0f;

        private List<string> captureIDs = null;

        private Size captureSize = new Size(800, 800);

        private Size resizeSizeEng = new Size(100, 100);

        private Size resizeSizeJpn = new Size(400, 400);

        private float cropOffsetX = 100.0f;

        private float cropOffsetY = -115.0f;

        private bool drawingPreview = false;
        private CheckBox disableLoadRemover;
        private NumericUpDown blacklevelValue;
        private bool synchronizingBlacklevel;
        private NumericUpDown captureLeft;
        private NumericUpDown captureTop;
        private NumericUpDown captureRight;
        private NumericUpDown captureBottom;
        private bool synchronizingCaptureRegion;

        private List<Control> dynamicAutoSplitterControls;

        private float featureVectorResolutionX = 1920.0f;

        private float featureVectorResolutionY = 1080.0f;

        private ImageCaptureInfo imageCaptureInfo;

        private Bitmap lastDiagnosticCapture = null;

        private List<int> lastFeatures = null;

        private Bitmap lastFullCapture = null;

        private Bitmap lastFullCroppedCapture = null;

        private int lastMatchingBins = 0;

        private LiveSplitState liveSplitState = null;

        //private string DiagnosticsFolderName = "CrashNSTDiagnostics/";
        private int numCaptures = 0;

        private int numScreens = 1;

        private Dictionary<string, XmlElement> AllGameAutoSplitSettings;

        private Bitmap previewImage = null;

        //-1 -> full screen, otherwise index process list
        private int processCaptureIndex = -1;

        private Process[] processList;
        private int scalingValue = 100;
        private float scalingValueFloat = 1.0f;
        private string selectedCaptureID = "";
        private Point selectionBottomRight = new Point(0, 0);
        private Rectangle selectionRectanglePreviewBox;
        private Point selectionTopLeft = new Point(0, 0);

        #endregion Private Fields

        #region Public Constructors

        public CrashTWoCLoadRemovalSettings(LiveSplitState state, bool capturePreview = true)
        {
            this.capturePreview = capturePreview;
            InitializeComponent();
            InitializeSetupControls();
            InitializeGraphicsCaptureSettings();
            InitializeMachineLearningSettings();

            //RemoveFadeins = chkRemoveFadeIns.Checked;
            //if (language == "ENG")
            //{
            //    languageComboBox.SelectedIndex = languageComboBox.FindStringExact("ENG");
            //} 
            SaveDetectionLog = chkSaveDetectionLog.Checked;
            platformComboBox.SelectedItem = platform;
            UpdateBlacklevelLabel();
            AllGameAutoSplitSettings = new Dictionary<string, XmlElement>();
            dynamicAutoSplitterControls = new List<Control>();
            CreateAutoSplitControls(state);
            liveSplitState = state;
            initImageCaptureInfo();
            //processListComboBox.SelectedIndex = 0;
            lblVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);


            RefreshCaptureWindowList();
            //processListComboBox.SelectedIndex = 0;
            DrawPreview();
        }

        #endregion Public Constructors

        #region Public Methods

        private Bitmap CaptureImageLegacy()
        {
            Bitmap b;

            //Full screen capture
            if (processCaptureIndex < 0)
            {
                Screen selected_screen = Screen.AllScreens[-processCaptureIndex - 1];
                Rectangle screenRect = selected_screen.Bounds;

                screenRect.Width = (int)(screenRect.Width * scalingValueFloat);
                screenRect.Height = (int)(screenRect.Height * scalingValueFloat);

                Point screenCenter = new Point(screenRect.Width / 2, screenRect.Height / 2);

                //Change size according to selected crop
                screenRect.Width = (int)(imageCaptureInfo.crop_coordinate_right - imageCaptureInfo.crop_coordinate_left);
                screenRect.Height = (int)(imageCaptureInfo.crop_coordinate_bottom - imageCaptureInfo.crop_coordinate_top);

                //Compute crop coordinates and width/ height based on resolution
                ImageCapture.SizeAdjustedCropAndOffset(screenRect.Width, screenRect.Height, ref imageCaptureInfo);

                //Adjust for crop offset
                imageCaptureInfo.center_of_frame_x += imageCaptureInfo.crop_coordinate_left;
                imageCaptureInfo.center_of_frame_y += imageCaptureInfo.crop_coordinate_top;

                //Adjust for selected screen offset
                imageCaptureInfo.center_of_frame_x += selected_screen.Bounds.X;
                imageCaptureInfo.center_of_frame_y += selected_screen.Bounds.Y;

                b = ImageCapture.CaptureFromDisplay(ref imageCaptureInfo, useResize: true);
            }
            else
            {
                IntPtr handle = new IntPtr(0);

                if (processList == null || processCaptureIndex >= processList.Length)
                    return new Bitmap(1, 1);

                if (processCaptureIndex != -1)
                {
                    processList[processCaptureIndex].Refresh();
                    handle = processList[processCaptureIndex].MainWindowHandle;
                }
                //Capture from specific process
                processList[processCaptureIndex].Refresh();
                if (handle == IntPtr.Zero)
                    return new Bitmap(1, 1);

                b = ImageCapture.PrintWindow(handle, ref imageCaptureInfo, useCrop: true, useResize: true);
            }

            return b;
        }

        private Bitmap CaptureImageFullPreviewLegacy(ref ImageCaptureInfo imageCaptureInfo, bool useCrop = false)
        {
            Bitmap b = new Bitmap(1, 1);

            //Full screen capture
            if (processCaptureIndex < 0)
            {
                Screen selected_screen = Screen.AllScreens[-processCaptureIndex - 1];
                Rectangle screenRect = selected_screen.Bounds;

                screenRect.Width = (int)(screenRect.Width * scalingValueFloat);
                screenRect.Height = (int)(screenRect.Height * scalingValueFloat);

                Point screenCenter = new Point((int)(screenRect.Width / 2.0f), (int)(screenRect.Height / 2.0f));

                if (useCrop)
                {
                    //Change size according to selected crop
                    screenRect.Width = (int)(imageCaptureInfo.crop_coordinate_right - imageCaptureInfo.crop_coordinate_left);
                    screenRect.Height = (int)(imageCaptureInfo.crop_coordinate_bottom - imageCaptureInfo.crop_coordinate_top);
                }

                //Compute crop coordinates and width/ height based on resoution
                ImageCapture.SizeAdjustedCropAndOffset(screenRect.Width, screenRect.Height, ref imageCaptureInfo);

                imageCaptureInfo.actual_crop_size_x = 2 * imageCaptureInfo.center_of_frame_x;
                imageCaptureInfo.actual_crop_size_y = 2 * imageCaptureInfo.center_of_frame_y;

                if (useCrop)
                {
                    //Adjust for crop offset
                    imageCaptureInfo.center_of_frame_x += imageCaptureInfo.crop_coordinate_left;
                    imageCaptureInfo.center_of_frame_y += imageCaptureInfo.crop_coordinate_top;
                }

                //Adjust for selected screen offset
                imageCaptureInfo.center_of_frame_x += selected_screen.Bounds.X;
                imageCaptureInfo.center_of_frame_y += selected_screen.Bounds.Y;

                imageCaptureInfo.actual_offset_x = 0;
                imageCaptureInfo.actual_offset_y = 0;

                b = ImageCapture.CaptureFromDisplay(ref imageCaptureInfo);

                imageCaptureInfo.actual_offset_x = cropOffsetX;
                imageCaptureInfo.actual_offset_y = cropOffsetY;
            }
            else
            {
                IntPtr handle = new IntPtr(0);

                if (processCaptureIndex >= processList.Length)
                    return b;

                if (processCaptureIndex != -1)
                {
                    processList[processCaptureIndex].Refresh();
                    handle = processList[processCaptureIndex].MainWindowHandle;
                }
                //Capture from specific process
                processList[processCaptureIndex].Refresh();
                if ((int)handle == 0)
                    return b;

                b = ImageCapture.PrintWindow(handle, ref imageCaptureInfo, full: true, useCrop: useCrop, scalingValueFloat: scalingValueFloat);
            }

            return b;
        }

        public void ChangeAutoSplitSettingsToGameName(string gameName, string category)
        {
            gameName = removeInvalidXMLCharacters(gameName);
            category = removeInvalidXMLCharacters(category);

            //TODO: go through gameSettings to see if the game matches, enter info based on that.
            foreach (var control in dynamicAutoSplitterControls)
            {
                tabPage2.Controls.Remove(control);
            }

            dynamicAutoSplitterControls.Clear();

            //Add current game to gameSettings
            XmlDocument document = new XmlDocument();

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            //var categoryNode = document.CreateElement(autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }


            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;

            //otherGameSettings[]

            CreateAutoSplitControls(liveSplitState);

            //Change controls if we find the chosen game
            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key == gameName + category)
                {
                    var game_element = gameSettings.Value;

                    //var splits_element = game_element[autoSplitData.Category];
                    Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                    foreach (XmlElement number_of_loads in game_element)
                    {
                        var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);
                        if (up_down_controls == null || up_down_controls.Length == 0) continue;

                        if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                        {
                            usedSplitNames[number_of_loads.LocalName] = 0;
                        }
                        else
                        {
                            usedSplitNames[number_of_loads.LocalName]++;
                        }

                        //var up_down = tabPage2.Controls.Find(number_of_loads.LocalName, true).FirstOrDefault() as NumericUpDown;

                        if (usedSplitNames[number_of_loads.LocalName] >= up_down_controls.Length) continue;
                        NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                        if (up_down != null)
                        {
                            up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                        }
                    }

                }
            }
        }
        public int GetCumulativeNumberOfLoadsForSplit(string splitName)
        {
            int numberOfLoads = 0;
            splitName = removeInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                numberOfLoads += entry.NumberOfLoads;
                if (entry.SplitName == splitName)
                {
                    return numberOfLoads;
                }
            }
            return numberOfLoads;
        }

        public int GetAutoSplitNumberOfLoadsForSplit(string splitName)
        {
            splitName = removeInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    return entry.NumberOfLoads;
                }
            }

            //This should never happen, but might if the splits are changed without reloading the component...
            return 2;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            //RefreshCaptureWindowList();
            var settingsNode = document.CreateElement("Settings");

            settingsNode.AppendChild(ToElement(document, "Version", Assembly.GetExecutingAssembly().GetName().Version.ToString(3)));


            if (captureIDs != null)
            {
                if (processListComboBox.SelectedIndex < captureIDs.Count && processListComboBox.SelectedIndex >= 0)
                {
                    var selectedCaptureTitle = captureIDs[processListComboBox.SelectedIndex];

                    settingsNode.AppendChild(ToElement(document, "SelectedCaptureTitle", selectedCaptureTitle));
                }
            }
            if (platformComboBox.SelectedItem != null)
            {
                settingsNode.AppendChild(ToElement(document, "platform", platformComboBox.SelectedItem.ToString()));
            }

            settingsNode.AppendChild(ToElement(document, "blacklevel", blacklevel.ToString()));
            settingsNode.AppendChild(ToElement(document, "LoadRemoverEnabled", LoadRemoverEnabled));
            settingsNode.AppendChild(ToElement(document, "UseWindowsGraphicsCapture", UseWindowsGraphicsCapture));

            settingsNode.AppendChild(ToElement(document, "ScalingPercent", trackBar1.Value));

            var captureRegionNode = document.CreateElement("CaptureRegion");

            captureRegionNode.AppendChild(ToElement(document, "X", selectionRectanglePreviewBox.X));
            captureRegionNode.AppendChild(ToElement(document, "Y", selectionRectanglePreviewBox.Y));
            captureRegionNode.AppendChild(ToElement(document, "Width", selectionRectanglePreviewBox.Width));
            captureRegionNode.AppendChild(ToElement(document, "Height", selectionRectanglePreviewBox.Height));

            settingsNode.AppendChild(captureRegionNode);

            settingsNode.AppendChild(ToElement(document, "AutoSplitEnabled", enableAutoSplitterChk.Checked));
            settingsNode.AppendChild(ToElement(document, "AutoSplitDisableOnSkipUntilSplit", chkAutoSplitterDisableOnSkip.Checked));
            //settingsNode.AppendChild(ToElement(document, "RemoveFadeins", chkRemoveFadeIns.Checked));
            settingsNode.AppendChild(ToElement(document, "SaveDetectionLog", chkSaveDetectionLog.Checked));
            SaveMachineLearningSettings(document, settingsNode);

            var splitsNode = document.CreateElement("AutoSplitGames");

            //Re-Add all other games/categories to the xml file
            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key != autoSplitData.GameName + autoSplitData.Category)
                {
                    XmlNode node = document.ImportNode(gameSettings.Value, true);
                    splitsNode.AppendChild(node);
                }
            }

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            //var categoryNode = document.CreateElement(autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }
            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;
            //gameNode.AppendChild(categoryNode);
            splitsNode.AppendChild(gameNode);
            settingsNode.AppendChild(splitsNode);
            //settingsNode.AppendChild(ToElement(document, "AutoReset", AutoReset.ToString()));
            //settingsNode.AppendChild(ToElement(document, "Category", category.ToString()));
            /*if (checkedListBox1.Items.Count == SplitsByCategory[category].Length)
			{
				for (int i = 0; i < checkedListBox1.Items.Count; i++)
				{
					SplitsByCategory[category][i].enabled = (checkedListBox1.GetItemCheckState(i) == CheckState.Checked);
				}
			}

			foreach (Split[] category in SplitsByCategory)
			{
				foreach (Split split in category)
				{
					settingsNode.AppendChild(ToElement(document, "split_" + split.splitID, split.enabled.ToString()));
				}
			}*/

            return settingsNode;
        }

        public void SetSettings(XmlNode settings)
        {
            applyingCaptureSettings = true;
            try
            {
            var element = (XmlElement)settings;
            bool graphicsCapture;
            useWindowsGraphicsCapture.Checked = element["UseWindowsGraphicsCapture"] != null
                && bool.TryParse(element["UseWindowsGraphicsCapture"].InnerText, out graphicsCapture) && graphicsCapture;
            bool enabled;
            disableLoadRemover.Checked = element["LoadRemoverEnabled"] != null
                && bool.TryParse(element["LoadRemoverEnabled"].InnerText, out enabled) && !enabled;
            LoadMachineLearningSettings(element);
            if (!element.IsEmpty)
            {
                Version version;
                if (element["Version"] != null)
                {
                    version = Version.Parse(element["Version"].InnerText);
                }
                else
                {
                    version = new Version(1, 0, 0);
                }

                if (element["SelectedCaptureTitle"] != null)
                {
                    String selectedCaptureTitle = element["SelectedCaptureTitle"].InnerText;
                    selectedCaptureID = selectedCaptureTitle;
                    RefreshCaptureWindowList(true);
                }

                if (element["platform"] != null)
                {
                    platformComboBox.SelectedItem = element["platform"].InnerText;
                    platform = platformComboBox.SelectedItem.ToString();
                    if (platform == "JPN/PS2" || platform == "KRN/PS2")
                    {
                        imageCaptureInfo.resizeState = ResizeState.JPN;
                    }
                    else
                    {
                        imageCaptureInfo.resizeState = ResizeState.ENG;
                    }
                }

                if (element["blacklevel"] != null)
                {
                    int savedBlacklevel;
                    blacklevel = int.TryParse(element["blacklevel"].InnerText, out savedBlacklevel)
                        && savedBlacklevel >= 0 && savedBlacklevel <= 255 ? savedBlacklevel : 100;
                }

                if (element["ScalingPercent"] != null)
                {
                    trackBar1.Value = Convert.ToInt32(element["ScalingPercent"].InnerText);
                }

                if (element["CaptureRegion"] != null)
                {
                    var element_region = element["CaptureRegion"];
                    int captureRegionX, captureRegionY, captureRegionWidth, captureRegionHeight;
                    if (element_region["X"] != null && element_region["Y"] != null && element_region["Width"] != null && element_region["Height"] != null
                        && int.TryParse(element_region["X"].InnerText, out captureRegionX)
                        && int.TryParse(element_region["Y"].InnerText, out captureRegionY)
                        && int.TryParse(element_region["Width"].InnerText, out captureRegionWidth)
                        && int.TryParse(element_region["Height"].InnerText, out captureRegionHeight))
                    {
                        SetCaptureRegion(new Rectangle(captureRegionX, captureRegionY, captureRegionWidth, captureRegionHeight));
                    }
                    else SetCaptureRegion(new Rectangle(Point.Empty, previewPictureBox.Size));
                }

                /*foreach (Split[] category in SplitsByCategory)
				{
					foreach (Split split in category)
					{
						if (element["split_" + split.splitID] != null)
						{
							split.enabled = Convert.ToBoolean(element["split_" + split.splitID].InnerText);
						}
					}
				}*/
                if (element["AutoSplitEnabled"] != null)
                {
                    enableAutoSplitterChk.Checked = Convert.ToBoolean(element["AutoSplitEnabled"].InnerText);
                }

                if (element["AutoSplitDisableOnSkipUntilSplit"] != null)
                {
                    chkAutoSplitterDisableOnSkip.Checked = Convert.ToBoolean(element["AutoSplitDisableOnSkipUntilSplit"].InnerText);
                }

                if (element["SaveDetectionLog"] != null)
                {
                    chkSaveDetectionLog.Checked = Convert.ToBoolean(element["SaveDetectionLog"].InnerText);
                }

                if (element["AutoSplitGames"] != null)
                {
                    var auto_split_element = element["AutoSplitGames"];

                    foreach (XmlElement game in auto_split_element)
                    {
                        if (game.LocalName != autoSplitData.GameName)
                        {
                            AllGameAutoSplitSettings[game.LocalName] = game;
                        }
                    }

                    if (auto_split_element[autoSplitData.GameName + autoSplitData.Category] != null)
                    {
                        var game_element = auto_split_element[autoSplitData.GameName + autoSplitData.Category];
                        AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = game_element;
                        //var splits_element = game_element[autoSplitData.Category];
                        Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                        foreach (XmlElement number_of_loads in game_element)
                        {
                            var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);

                            //This can happen if the layout was not saved and contains old splits.
                            if (up_down_controls == null || up_down_controls.Length == 0)
                            {
                                continue;
                            }

                            if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                            {
                                usedSplitNames[number_of_loads.LocalName] = 0;
                            }
                            else
                            {
                                usedSplitNames[number_of_loads.LocalName]++;
                            }

                            //var up_down = tabPage2.Controls.Find(number_of_loads.LocalName, true).FirstOrDefault() as NumericUpDown;

                            NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                            if (up_down != null)
                            {
                                up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                            }
                        }
                    }
                }

                DrawPreview();
            }
            UpdateBlacklevelLabel();
            }
            finally { applyingCaptureSettings = false; }
            ResetGraphicsCapture();
            UpdateCaptureBackendControls();
            NotifyDetectionConfigurationChanged();
            DrawPreview();
        }

        #endregion Public Methods

        #region Private Methods

        private void AutoSplitUpDown_ValueChanged(object sender, EventArgs e, string splitName)
        {
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    entry.NumberOfLoads = (int)((NumericUpDown)sender).Value;
                    return;
                }
            }
        }


        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (refreshingCaptureSources) return;
            ResetGraphicsCapture();
            NotifyDetectionConfigurationChanged();
            if (processListComboBox.SelectedIndex < 0)
            {
                UpdateCaptureStatus("The selected capture source is unavailable.", true);
                return;
            }
            if (processListComboBox.SelectedIndex < numScreens)
            {
                processCaptureIndex = -processListComboBox.SelectedIndex - 1;
            }
            else
            {
                processCaptureIndex = processListComboBox.SelectedIndex - numScreens;
            }

            //selectionTopLeft = new Point(0, 0);
            //selectionBottomRight = new Point(previewPictureBox.Width, previewPictureBox.Height);

            SetCaptureRegion(Rectangle.FromLTRB(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X, selectionBottomRight.Y));

            //Console.WriteLine("SELECTED ITEM: {0}", processListComboBox.SelectedItem.ToString());
            DrawPreview();
        }

        private void CreateAutoSplitControls(LiveSplitState state)
        {
            autoSplitCategoryLbl.Text = "Category: " + state.Run.CategoryName;
            autoSplitNameLbl.Text = "Game: " + state.Run.GameName;

            int splitOffsetY = 95;
            int splitSpacing = 50;

            int splitCounter = 0;
            autoSplitData = new AutoSplitData(removeInvalidXMLCharacters(state.Run.GameName), removeInvalidXMLCharacters(state.Run.CategoryName));

            foreach (var split in state.Run)
            {
                //Setup controls for changing AutoSplit settings
                var autoSplitPanel = new System.Windows.Forms.Panel();
                var autoSplitLbl = new System.Windows.Forms.Label();
                var autoSplitUpDown = new System.Windows.Forms.NumericUpDown();

                autoSplitUpDown.Value = 2;
                autoSplitPanel.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
                autoSplitPanel.Controls.Add(autoSplitUpDown);
                autoSplitPanel.Controls.Add(autoSplitLbl);
                autoSplitPanel.Location = new System.Drawing.Point(28, splitOffsetY + splitSpacing * splitCounter);
                autoSplitPanel.Size = new System.Drawing.Size(409, 39);

                autoSplitLbl.AutoSize = true;
                autoSplitLbl.Location = new System.Drawing.Point(3, 10);
                autoSplitLbl.Size = new System.Drawing.Size(199, 13);
                autoSplitLbl.TabIndex = 0;
                autoSplitLbl.Text = split.Name;

                autoSplitUpDown.Location = new System.Drawing.Point(367, 8);
                autoSplitUpDown.Size = new System.Drawing.Size(35, 20);
                autoSplitUpDown.TabIndex = 1;

                //Remove all whitespace to name the control, we can then access it in SetSettings.
                autoSplitUpDown.Name = removeInvalidXMLCharacters(split.Name);

                autoSplitUpDown.ValueChanged += (s, e) => AutoSplitUpDown_ValueChanged(autoSplitUpDown, e, removeInvalidXMLCharacters(split.Name));

                tabPage2.Controls.Add(autoSplitPanel);
                //tabPage2.Controls.Add(autoSplitLbl);
                //tabPage2.Controls.Add(autoSplitUpDown);

                autoSplitData.SplitData.Add(new AutoSplitEntry(removeInvalidXMLCharacters(split.Name), 2));
                dynamicAutoSplitterControls.Add(autoSplitPanel);
                splitCounter++;
            }
        }

        private void DrawCaptureRectangleBitmap()
        {
            if (previewImage == null) return;
            Bitmap capture_image = (Bitmap)previewImage.Clone();
            //Draw selection rectangle
            using (Graphics g = Graphics.FromImage(capture_image))
            {
                using (Pen drawing_pen = new Pen(Color.Magenta, 8.0f))
                {
                    drawing_pen.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(drawing_pen, selectionRectanglePreviewBox);
                }
            }

            var oldPreview = previewPictureBox.Image;
            previewPictureBox.Image = capture_image;
            if (oldPreview != null) oldPreview.Dispose();
        }

        private void DrawPreview()
        {
            if (!capturePreview || applyingCaptureSettings || refreshingCaptureSources) return;
            StopCapturePreviewRetry();
            try
            {


                ClearCapturePreview();
                ImageCaptureInfo copy = imageCaptureInfo;
                copy.captureSizeX = previewPictureBox.Width;
                copy.captureSizeY = previewPictureBox.Height;

                //Show something in the preview
                previewImage = CaptureImageFullPreview(ref copy);
                float crop_size_x = copy.actual_crop_size_x;
                float crop_size_y = copy.actual_crop_size_y;

                lastFullCapture = previewImage;
                //Draw selection rectangle
                DrawCaptureRectangleBitmap();

                //Compute image crop coordinates according to selection rectangle

                //Get raw image size from imageCaptureInfo.actual_crop_size to compute scaling between raw and rectangle coordinates

                //Console.WriteLine("SIZE X: {0}, SIZE Y: {1}", imageCaptureInfo.actual_crop_size_x, imageCaptureInfo.actual_crop_size_y);

                imageCaptureInfo.crop_coordinate_left = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.crop_coordinate_right = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.crop_coordinate_top = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                imageCaptureInfo.crop_coordinate_bottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                copy.crop_coordinate_left = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                copy.crop_coordinate_right = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                copy.crop_coordinate_top = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                copy.crop_coordinate_bottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                Bitmap full_cropped_capture = CaptureImageFullPreview(ref copy, useCrop: true);
                croppedPreviewPictureBox.Image = full_cropped_capture;
                lastFullCroppedCapture = full_cropped_capture;

                copy.captureSizeX = captureSize.Width;
                copy.captureSizeY = captureSize.Height;

                //Show matching bins for preview
                using (var capture = CaptureImage()) { }
            }
            catch (Exception ex)
            {
                ClearCapturePreview();
                UpdateCaptureStatus(ex.Message, !(ex is CaptureNotReadyException));
                if (ex is CaptureNotReadyException) RetryPendingCapturePreview();
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void enableAutoSplitterChk_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterEnabled = enableAutoSplitterChk.Checked;
        }

        private void initImageCaptureInfo()
        {
            imageCaptureInfo = new ImageCaptureInfo();

            SetCaptureRegion(new Rectangle(Point.Empty, previewPictureBox.Size));

            imageCaptureInfo.featureVectorResolutionX = featureVectorResolutionX;
            imageCaptureInfo.featureVectorResolutionY = featureVectorResolutionY;
            imageCaptureInfo.captureSizeX = captureSize.Width;
            imageCaptureInfo.captureSizeY = captureSize.Height;
            imageCaptureInfo.resizeSizeXEng = resizeSizeEng.Width;
            imageCaptureInfo.resizeSizeYEng = resizeSizeEng.Height;
            imageCaptureInfo.resizeSizeXJpn = resizeSizeJpn.Width;
            imageCaptureInfo.resizeSizeYJpn = resizeSizeJpn.Height;
            imageCaptureInfo.resizeState = ResizeState.ENG;
            imageCaptureInfo.cropOffsetX = cropOffsetX;
            imageCaptureInfo.cropOffsetY = cropOffsetY;
            imageCaptureInfo.captureAspectRatio = captureAspectRatioX / captureAspectRatioY;
        }

        private void previewPictureBox_MouseClick(object sender, MouseEventArgs e)
        {
        }

        private void previewPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void previewPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            if (drawingPreview == false)
            {
                drawingPreview = true;
                //Draw selection rectangle
                DrawCaptureRectangleBitmap();
                drawingPreview = false;
            }
        }

        private void previewPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void processListComboBox_DropDown(object sender, EventArgs e)
        {
            RefreshCaptureWindowList();
            //processListComboBox.SelectedIndex = 0;
        }

        private void RefreshCaptureWindowList(bool preserveSelection = false)
        {
            refreshingCaptureSources = true;
            try
            {
                Process[] processListtmp = Process.GetProcesses();
                List<Process> processes_with_name = new List<Process>();

                if (!preserveSelection && captureIDs != null)
                {
                    if (processListComboBox.SelectedIndex < captureIDs.Count && processListComboBox.SelectedIndex >= 0)
                    {
                        selectedCaptureID = captureIDs[processListComboBox.SelectedIndex];
                    }
                }

                captureIDs = new List<string>();

                processListComboBox.Items.Clear();
                numScreens = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    // For each screen, add the screen properties to a list box.
                    processListComboBox.Items.Add("Screen: " + screen.DeviceName + ", " + screen.Bounds.ToString());
                    captureIDs.Add("Screen: " + screen.DeviceName);
                    numScreens++;
                }
                foreach (Process process in processListtmp)
                {
                    if (!String.IsNullOrEmpty(process.MainWindowTitle))
                    {
                        //Console.WriteLine("Process: {0} ID: {1} Window title: {2} HWND PTR {3}", process.ProcessName, process.Id, process.MainWindowTitle, process.MainWindowHandle);
                        processListComboBox.Items.Add(process.ProcessName + ": " + process.MainWindowTitle);
                        captureIDs.Add(process.ProcessName);
                        processes_with_name.Add(process);
                    }
                }

                processList = processes_with_name.ToArray();
                UpdateIndexToCaptureID();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
            finally { refreshingCaptureSources = false; }
            comboBox1_SelectedIndexChanged(this, EventArgs.Empty);
        }

        public string removeInvalidXMLCharacters(string in_string)
        {
            if (in_string == null) return null;

            StringBuilder sbOutput = new StringBuilder();
            char ch;

            bool was_other_char = false;

            for (int i = 0; i < in_string.Length; i++)
            {
                ch = in_string[i];

                if ((ch >= 0x0 && ch <= 0x2F) ||
                    (ch >= 0x3A && ch <= 0x40) ||
                    (ch >= 0x5B && ch <= 0x60) ||
                    (ch >= 0x7B)
                    )
                {
                    continue;
                }

                //Can't start with a number.
                if (was_other_char == false && ch >= '0' && ch <= '9')
                {
                    continue;
                }

                /*if ((ch >= 0x0020 && ch <= 0xD7FF) ||
					(ch >= 0xE000 && ch <= 0xFFFD) ||
					ch == 0x0009 ||
					ch == 0x000A ||
					ch == 0x000D)
				{*/
                sbOutput.Append(ch);
                was_other_char = true;
                //}
            }

            if (sbOutput.Length == 0)
            {
                sbOutput.Append("NULL");
            }

            return sbOutput.ToString();
        }

        private void saveDiagnosticsButton_Click(object sender, EventArgs e)
        {
            try
            {
                FolderBrowserDialog fbd = new FolderBrowserDialog();

                var result = fbd.ShowDialog();

                if (result != DialogResult.OK)
                {
                    return;
                }

                //System.IO.Directory.CreateDirectory(fbd.SelectedPath);
                numCaptures++;
                lastFullCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_FULL_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                lastFullCroppedCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_FULL_CROPPED_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                lastDiagnosticCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_DIAGNOSTIC_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                saveFeatureVectorToTxt(lastFeatures, numCaptures.ToString() + "_FEATURES_" + lastMatchingBins + ".txt", fbd.SelectedPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void saveFeatureVectorToTxt(List<int> featureVector, string filename, string directoryName)
        {
            System.IO.Directory.CreateDirectory(directoryName);
            try
            {
                using (var file = File.CreateText(directoryName + "/" + filename))
                {
                    file.Write("{");
                    file.Write(string.Join(",", featureVector));
                    file.Write("},\n");
                }
            }
            catch
            {
                //yeah, silent catch is bad, I don't care
            }
        }

        private void SetRectangleFromMouse(MouseEventArgs e)
        {
            //Clamp values to pictureBox range
            int x = Math.Min(Math.Max(0, e.Location.X), previewPictureBox.Width);
            int y = Math.Min(Math.Max(0, e.Location.Y), previewPictureBox.Height);

            if (e.Button == MouseButtons.Left
                && (selectionRectanglePreviewBox.Left + selectionRectanglePreviewBox.Width) - x > 0
                && (selectionRectanglePreviewBox.Top + selectionRectanglePreviewBox.Height) - y > 0)
            {
                selectionTopLeft = new Point(x, y);
            }
            else if (e.Button == MouseButtons.Right && x - selectionRectanglePreviewBox.Left > 0 && y - selectionRectanglePreviewBox.Top > 0)
            {
                selectionBottomRight = new Point(x, y);
            }

            SetCaptureRegion(Rectangle.FromLTRB(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X, selectionBottomRight.Y));
        }

        private XmlElement ToElement<T>(XmlDocument document, String name, T value)
        {
            var element = document.CreateElement(name);
            element.InnerText = value.ToString();
            return element;
        }

        private void trackBar1_ValueChanged(object sender, EventArgs e)
        {
            scalingValue = trackBar1.Value;

            if (scalingValue % trackBar1.SmallChange != 0)
            {
                scalingValue = (scalingValue / trackBar1.SmallChange) * trackBar1.SmallChange;

                trackBar1.Value = scalingValue;
            }

            scalingValueFloat = ((float)scalingValue) / 100.0f;

            UpdateCaptureBackendControls();
            NotifyDetectionConfigurationChanged();

            DrawPreview();
        }

        private void UpdateIndexToCaptureID()
        {
            if (String.IsNullOrEmpty(selectedCaptureID))
            {
                processListComboBox.SelectedIndex = numScreens > 0 ? 0 : -1;
                return;
            }
            int matchingIndex = captureIDs == null ? -1
                : captureIDs.FindIndex(id => String.Equals(id, selectedCaptureID, StringComparison.Ordinal));
            processListComboBox.SelectedIndex = matchingIndex >= 0 && matchingIndex < processListComboBox.Items.Count
                ? matchingIndex : -1;
        }

        private void updatePreviewButton_Click(object sender, EventArgs e)
        {

            DrawPreview();
        }

        #endregion Private Methods

        private void chkAutoSplitterDisableOnSkip_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterDisableOnSkipUntilSplit = chkAutoSplitterDisableOnSkip.Checked;
        }

        private void chkSaveDetectionLog_CheckedChanged(object sender, EventArgs e)
        {
            SaveDetectionLog = chkSaveDetectionLog.Checked;
        }


        private void saveCutout_Click(object sender, EventArgs e)
        {
            Bitmap Cutout = CaptureImage();
            //Cutout = ImageCapture.CropImage(Cutout);
            Cutout.Save("cutout.bmp");
        }

        private void languageComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            platform = platformComboBox.SelectedItem.ToString();
            if (platform == "ENG/PS2" || platform == "ENG/XBOX&GC" || platform == ExperimentalMlPlatform)
            {
                imageCaptureInfo.resizeState = ResizeState.ENG;
            }
            else
            {
                imageCaptureInfo.resizeState = ResizeState.JPN;
            }
        }

        private void CalibrateBlacklevelButton_Click(object sender, EventArgs e)
        {
            if (!LoadRemoverEnabled || TrainingCaptureActive) return;
            NotifyDetectionConfigurationChanged();
            isCalibratingBlacklevel = true;
            blacklevel = 100;
            cmpBlackLevel = 100;
            UpdateBlacklevelLabel();
        }

        public void UpdateBlacklevelLabel ()
        {
            if (IsDisposed) return;
            synchronizingBlacklevel = true;
            try { blacklevelValue.Value = Math.Max(0, Math.Min(255, blacklevel)); }
            finally { synchronizingBlacklevel = false; }
            blacklevelLabel.Text = isCalibratingBlacklevel ? "Calibrating: wait for loading text..." : "Black level: 0–255";
            blacklevelLabel.ForeColor = SystemColors.ControlText;
            calibrateBlacklevelButton.Enabled = LoadRemoverEnabled && !TrainingCaptureActive;
        }

        private void InitializeSetupControls()
        {
            tabPage1.AutoScroll = true;
            disableLoadRemover = new CheckBox { Name = "DisableLoadRemover", Text = "Disable load remover", AutoSize = true, Location = new Point(30, 62) };
            disableLoadRemover.CheckedChanged += (sender, args) =>
            {
                if (!LoadRemoverEnabled) ResetGraphicsCapture();
                isCalibratingBlacklevel = false;
                cmpBlackLevel = 100;
                UpdateBlacklevelLabel();
                if (LoadRemoverEnabledChanged != null) LoadRemoverEnabledChanged(this, EventArgs.Empty);
            };
            tabPage1.Controls.Add(disableLoadRemover);
            panel1.Location = new Point(30, 88);
            label8.Location = new Point(30, 151);
            blacklevelValue = new NumericUpDown { Name = "BlacklevelValue", Minimum = 0, Maximum = 255, Value = blacklevel, Location = new Point(95, 147), Size = new Size(70, 22) };
            blacklevelValue.ValueChanged += (sender, args) =>
            {
                if (synchronizingBlacklevel) return;
                blacklevel = (int)blacklevelValue.Value;
                isCalibratingBlacklevel = false;
                cmpBlackLevel = 100;
                NotifyDetectionConfigurationChanged();
                UpdateBlacklevelLabel();
            };
            tabPage1.Controls.Add(blacklevelValue);
            chkSaveDetectionLog.Location = new Point(185, 151);
            calibrateBlacklevelButton.Location = new Point(304, 147);
            blacklevelLabel.Location = new Point(30, 177);
            tabPage1.Controls.Add(new Label { Text = "Capture boundaries (400 × 160 preview coordinates):", AutoSize = true, Location = new Point(30, 204) });
            tabPage1.Controls.Add(new Label { Text = "Top-left X:", AutoSize = true, Location = new Point(30, 232) });
            tabPage1.Controls.Add(new Label { Text = "Y:", AutoSize = true, Location = new Point(180, 232) });
            tabPage1.Controls.Add(new Label { Text = "Bottom-right X:", AutoSize = true, Location = new Point(30, 261) });
            tabPage1.Controls.Add(new Label { Text = "Y:", AutoSize = true, Location = new Point(180, 261) });
            captureLeft = CreateCaptureBoundaryControl("CaptureLeft", previewPictureBox.Width, 0, 112, 228);
            captureTop = CreateCaptureBoundaryControl("CaptureTop", previewPictureBox.Height, 0, 202, 228);
            captureRight = CreateCaptureBoundaryControl("CaptureRight", previewPictureBox.Width, previewPictureBox.Width, 112, 257);
            captureBottom = CreateCaptureBoundaryControl("CaptureBottom", previewPictureBox.Height, previewPictureBox.Height, 202, 257);
            label2.Location = new Point(28, 291);
            label11.Location = new Point(30, 312);
            previewPictureBox.Location = new Point(30, 333);
            label4.Location = new Point(28, 501);
            croppedPreviewPictureBox.Location = new Point(30, 524);
            lblVersion.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            lblVersion.Location = new Point(390, 694);
            tabPage1.AutoScrollMinSize = new Size(0, 717);
        }

        private NumericUpDown CreateCaptureBoundaryControl(string name, int maximum, int value, int x, int y)
        {
            var control = new NumericUpDown { Name = name, Minimum = 0, Maximum = maximum, Value = value, Increment = 1, Location = new Point(x, y), Size = new Size(62, 22) };
            control.ValueChanged += CaptureBoundaryValueChanged;
            tabPage1.Controls.Add(control);
            return control;
        }

        private void CaptureBoundaryValueChanged(object sender, EventArgs args)
        {
            if (synchronizingCaptureRegion) return;
            if (captureLeft.Value >= captureRight.Value || captureTop.Value >= captureBottom.Value)
            {
                SynchronizeCaptureBoundaryControls();
                return;
            }
            SetCaptureRegion(Rectangle.FromLTRB((int)captureLeft.Value, (int)captureTop.Value, (int)captureRight.Value, (int)captureBottom.Value));
            DrawPreview();
        }

        private void SetCaptureRegion(Rectangle region)
        {
            int left = Math.Max(0, Math.Min(previewPictureBox.Width - 1, region.X));
            int top = Math.Max(0, Math.Min(previewPictureBox.Height - 1, region.Y));
            int right = (int)Math.Max(left + 1L, Math.Min(previewPictureBox.Width, (long)region.X + Math.Max(1, region.Width)));
            int bottom = (int)Math.Max(top + 1L, Math.Min(previewPictureBox.Height, (long)region.Y + Math.Max(1, region.Height)));
            var validRegion = Rectangle.FromLTRB(left, top, right, bottom);
            bool changed = selectionRectanglePreviewBox != validRegion;
            selectionRectanglePreviewBox = validRegion;
            selectionTopLeft = validRegion.Location;
            selectionBottomRight = new Point(right, bottom);
            SynchronizeCaptureBoundaryControls();
            if (changed) NotifyDetectionConfigurationChanged();
        }

        private void SynchronizeCaptureBoundaryControls()
        {
            synchronizingCaptureRegion = true;
            try
            {
                captureLeft.Value = selectionRectanglePreviewBox.Left;
                captureTop.Value = selectionRectanglePreviewBox.Top;
                captureRight.Value = selectionRectanglePreviewBox.Right;
                captureBottom.Value = selectionRectanglePreviewBox.Bottom;
            }
            finally { synchronizingCaptureRegion = false; }
        }

        private void NotifyDetectionConfigurationChanged()
        {
            if (!applyingCaptureSettings && DetectionConfigurationChanged != null) DetectionConfigurationChanged(this, EventArgs.Empty);
        }

    }
    public class AutoSplitData
    {
        #region Public Fields

        public string Category;
        public string GameName;
        public List<AutoSplitEntry> SplitData;

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitData(string gameName, string category)
        {
            SplitData = new List<AutoSplitEntry>();
            GameName = gameName;
            Category = category;
        }

        #endregion Public Constructors
    }

    public class AutoSplitEntry
    {
        #region Public Fields

        public int NumberOfLoads = 2;
        public string SplitName = "";

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitEntry(string splitName, int numberOfLoads)
        {
            SplitName = splitName;
            NumberOfLoads = numberOfLoads;
        }

        #endregion Public Constructors
    }
}
