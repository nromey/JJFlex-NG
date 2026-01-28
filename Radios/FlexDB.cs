using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using System.IO.Compression;
using JJTrace;

namespace Radios
{
    class FlexDB
    {
        private const string filePrefix = "SSDR_Config";
        private const string exportFileTitle = "Export File";
        private const string importFileTitle = "Import File";
        private const string noFileMsg = "no import file found.";
        private const string errHdr = "Error";
        private const string statusHdr = "Status";
        private const string exportedMsg = "Export complete";
        private const string exportFailMsg = "Export failed - ";
        private const string exportTimeout = "timed out";
        private const string onlyPrimary = "Import/export is only allowed on the primary station.";
        private const string onlyLAN = "Currently, import is only allowed over the LAN.";

        private FlexBase rig;
        private string directoryName { get { return rig.ConfigDirectory + '\\' + rig.OperatorName + '\\' + "RigData"; } }
        private string metafileName { get { return "meta.txt"; } }

        public FlexDB(FlexBase r)
        {
            rig = r;
        }

        private List<string> getRigExportFiles()
        {
            List<string> rv = new List<string>();

            List<string> fileList = new List<string>();
            fileList.AddRange(Directory.GetFiles(directoryName));
            foreach (string f in fileList)
            {
                string baseName = f.Substring(f.LastIndexOf('\\') + 1);
                if ((baseName.Length > filePrefix.Length) &&
                    (baseName.Substring(0, filePrefix.Length) == filePrefix))
                {
                    rv.Add(f);
                }
            }
            return rv;
        }

        public bool Export()
        {
            Tracing.TraceLine("Flex export:", TraceLevel.Info);
            bool rv = false;
            string tmpDir = directoryName + "\\tmp";
            string tmpMeta = tmpDir + "\\tmpMeta";
            // Get export file name.
            GetFile theForm = new GetFile(exportFileTitle, "ssdr_cfg", true);
            DialogResult rslt = theForm.ShowDialog();
            string exportFile = theForm.FileName;
            if (rslt == DialogResult.Cancel) goto exportDone;

            try
            {
                if (!Directory.Exists(directoryName))
                {
                    Tracing.TraceLine("Flex export creating:" + directoryName, TraceLevel.Info);
                    Directory.CreateDirectory(directoryName);
                }

                List<string> fileList = getRigExportFiles();
                foreach (string f in fileList)
                {
                    Tracing.TraceLine("Flex Export deleting file:" + f, TraceLevel.Info);
                    File.Delete(f);
                }

                // Now create the meta file.
                if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);
                if (File.Exists(tmpMeta)) File.Delete(tmpMeta);

                using (StreamWriter sw = new StreamWriter(tmpMeta, true))
                {
                    //sw.WriteLine("GLOBAL_PROFILES^" + FlexBase.JJRadioDefault + '^');
                    foreach (string name in rig.theRadio.ProfileGlobalList)
                    {
                        sw.WriteLine("GLOBAL_PROFILES^" + name + '^');
                        Tracing.TraceLine("Flex Export:exporting GLOBAL_PROFILES^" + name + '^', TraceLevel.Info);
                    }
                    //sw.WriteLine("TX_PROFILES^" + FlexBase.JJRadioDefault + '^');
                    foreach (string name in rig.theRadio.ProfileTXList)
                    {
                        sw.WriteLine("TX_PROFILES^" + name + '^');
                        Tracing.TraceLine("Flex Export:exporting TX_PROFILES^" + name + '^', TraceLevel.Info);
                    }

                    // Export memories with an owner.
                    if (rig.theRadio.MemoryList.Count > 0)
                    {
                        string memString = "";
                        List<string> memNames = new List<string>(); // for dup checking
                        foreach (Memory m in rig.theRadio.MemoryList)
                        {
                            string tmpStr = null;
                            if (!string.IsNullOrEmpty(m.Owner))
                            {
                                tmpStr = m.Owner + '|' + ((m.Group != null) ? m.Group : "") + '^';
                                // Add if unique.
                                if (!memNames.Contains(tmpStr))
                                {
                                    memNames.Add(tmpStr);
                                    memString += tmpStr;
                                }
                            }
                        }
                        if (memString != "")
                        {
                            Tracing.TraceLine("Flex Export adding memories:" + memString, TraceLevel.Info);
                            sw.WriteLine("MEMORIES^" + memString);
                        }
                    }

                    Tracing.TraceLine("Flex Export adding BAND_PERSISTENCE", TraceLevel.Info);
                    sw.WriteLine("BAND_PERSISTENCE^");
                    Tracing.TraceLine("Flex Export adding Mode_PERSISTENCE", TraceLevel.Info);
                    sw.WriteLine("MODE_PERSISTENCE^");
                    Tracing.TraceLine("Flex Export adding Global_PERSISTENCE", TraceLevel.Info);
                    sw.WriteLine("GLOBAL_PERSISTENCE^");
                    if (rig.TNFs.Count > 0)
                    {
                        Tracing.TraceLine("Flex Export adding TNFs", TraceLevel.Info);
                        sw.WriteLine("TNFS^");
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex, true);
                return rv;
            }

            // Now, export the data.
            rig.ExportComplete = false;
            rig.ExportException = "";
            Tracing.TraceLine("Flex Export exporting...", TraceLevel.Info);
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.theRadio.ReceiveSSDRDatabaseFile(tmpMeta, directoryName, false); }));
            if (!FlexBase.await(() => { return rig.ExportComplete; }, 120000))
            {
                MessageBox.Show(exportFailMsg + exportTimeout, errHdr, MessageBoxButtons.OK);
            }
            else if (!string.IsNullOrEmpty(rig.ExportException))
            {
                MessageBox.Show(exportFailMsg + rig.ExportException, errHdr, MessageBoxButtons.OK);
            }
            else
            {
                rv = true;
                MessageBox.Show(exportedMsg, statusHdr, MessageBoxButtons.OK);
            }

            if (!string.IsNullOrEmpty(exportFile))
            {
                foreach(string f in getRigExportFiles())
                {
                    if (File.Exists(exportFile)) File.Delete(exportFile);
                    File.Copy(f, exportFile);
                    break;
                }
            }

            exportDone:
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            theForm.Dispose();
            return rv;
        }

        public bool Import()
        {
            Tracing.TraceLine("Flex import:", TraceLevel.Info);

            bool rv = true;
            GetFile theForm = new GetFile(importFileTitle, "ssdr_cfg");
            DialogResult rslt = theForm.ShowDialog();
            string theFile = theForm.FileName;
            if ((rslt == DialogResult.Cancel) |
                (theFile == null) |
                !File.Exists(theFile))
            {
                MessageBox.Show(noFileMsg, errHdr, MessageBoxButtons.OK);
                return false;
            }

            string tmpDir = directoryName + "\\tmp";
            string workingDir = Directory.GetCurrentDirectory();
            try
            {
                // unzip the flex_payload and meta_data files into a temp directory.
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                Directory.CreateDirectory(tmpDir);
                using (ZipArchive archive = ZipFile.OpenRead(theFile))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(tmpDir, entry.FullName));
                        if (!destinationPath.StartsWith(Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Entry outside target dir: " + entry.FullName);
                        string destDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                        if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }
                }
                if (!File.Exists(tmpDir + "\\meta_data"))
                {
                    throw new Exception("The archive must contain at least a meta_data file.");
                }
                // rename meta_data to meta_subset.
                File.Move(tmpDir + "\\meta_data", tmpDir + "\\meta_subset");
                // zip files to a temporary archive.
                string tmpZip = Path.Combine(tmpDir, "archive.zip");
                using (ZipArchive archive = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
                {
                    foreach (string f in Directory.GetFiles(tmpDir))
                    {
                        string entryName = Path.GetFileName(f);
                        archive.CreateEntryFromFile(f, entryName, CompressionLevel.Optimal);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex);
                rv = false;
            }
            finally
            {
                Directory.SetCurrentDirectory(workingDir);
            }
            if (rv)
            {
                string tmpZip = tmpDir + "\\archive.zip";
                Tracing.TraceLine("Flex import file:" + tmpZip, TraceLevel.Info);
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.ImportProfile(tmpZip); }));
                // Indicate radio is inactive.
                //rig.raisePowerOff();
                //rig.theRadio.DatabaseImportComplete = false;
                //rig.q.Enqueue((Flex.FunctionDel)(() => { rig.theRadio.SendDBImportFile(tmpZip); }));
            }

            //if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            return rv;
        }
    }
}
