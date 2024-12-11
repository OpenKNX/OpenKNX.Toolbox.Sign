using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenKNX.Toolbox.Sign
{
    public class SignHelper
    {
        private enum DocumentCategory
        {
            None,
            Catalog,
            Hardware,
            Application
        }
        
        public static async Task<int> ExportKnxprodAsync(string iWorkingDir, string iKnxprodFileName, string lTempXmlFileName, string iXsdFileName, bool iIsDebug, bool iAutoXsd)
        {
            int result = 0;
            Task runner = Task.Run(() => {
                result = ExportKnxprod(iWorkingDir, iKnxprodFileName, lTempXmlFileName, iXsdFileName, iIsDebug, iAutoXsd);
            });
            await runner;
            if(runner.Exception != null)
                throw runner.Exception;
            return result;
        }

        public static int ExportKnxprod(string iWorkingDir, string iKnxprodFileName, string lTempXmlFileName, string iXsdFileName, bool iIsDebug, bool iAutoXsd)
        {
            string outputFolder = AppDomain.CurrentDomain.BaseDirectory;
            if(Directory.Exists(Path.Combine(outputFolder, "Storage")))
                outputFolder = Path.Combine(outputFolder, "Storage", "Temp");
            else
                outputFolder = Path.Combine(outputFolder, "Temp");

            if (Directory.Exists(outputFolder))
                Directory.Delete(outputFolder, true);

            string manuId = GetManuId(lTempXmlFileName);
            if(string.IsNullOrEmpty(manuId))
            {
                Console.WriteLine("Could not find ManuId in xml");
                return -1;
            }
            Directory.CreateDirectory(outputFolder);
            Directory.CreateDirectory(Path.Combine(outputFolder, manuId));

            SplitXml(lTempXmlFileName, outputFolder);

            string iBaggageName = "";
            foreach(string dir in Directory.GetDirectories(iWorkingDir))
                if(dir.EndsWith(".baggages"))
                    iBaggageName = dir.Substring(dir.LastIndexOf(Path.PathSeparator)+1);

            if(!string.IsNullOrEmpty(iBaggageName))
                CopyBaggages(iWorkingDir, iBaggageName, outputFolder, manuId);

            string content = File.ReadAllText(lTempXmlFileName);
            Regex regex = new Regex("http://knx.org/xml/project/([0-9]{2})");
            Match m = regex.Match(content);
            int ns;
            if(m.Success)
                ns = int.Parse(m.Groups[1].Value);
            else
                throw new Exception("NameSpaceVersion konnte nicht ermittelt werden.");

            SignFiles(outputFolder, manuId, ns);
            CheckMaster(outputFolder, ns).Wait(); // TODO get real nsVersion
            ZipFolder(outputFolder, iKnxprodFileName);
            return 0;
        }

        public static void SignFiles(string outputFolder, string manuId, int ns)
        {
            string iPathETS = FindEtsPath(ns);
            IDictionary<string, string> applProgIdMappings = new Dictionary<string, string>();
            IDictionary<string, string> applProgHashes = new Dictionary<string, string>();
            IDictionary<string, string> mapBaggageIdToFileIntegrity = new Dictionary<string, string>(50);

            FileInfo hwFileInfo = new FileInfo(Path.Combine(outputFolder, manuId, "Hardware.xml"));
            FileInfo catalogFileInfo = new FileInfo(Path.Combine(outputFolder, manuId, "Catalog.xml"));

            string appFile = "";
            foreach(string file in Directory.GetFiles(Path.Combine(outputFolder, manuId)))
            {
                if(file.Contains(manuId + "_"))
                {
                    appFile = file;
                    break;
                }
            }
            FileInfo appInfo = new FileInfo(appFile);

            ApplicationProgramHasher aph = new ApplicationProgramHasher(appInfo, mapBaggageIdToFileIntegrity, iPathETS, ns, true);
            aph.HashFile();

            applProgIdMappings.Add(aph.OldApplProgId, aph.NewApplProgId);
            if (!applProgHashes.ContainsKey(aph.NewApplProgId))
                applProgHashes.Add(aph.NewApplProgId, aph.GeneratedHashString);

            HardwareSigner hws = new HardwareSigner(hwFileInfo, applProgIdMappings, applProgHashes, iPathETS, ns, true);
            hws.SignFile();
            IDictionary<string, string> hardware2ProgramIdMapping = hws.OldNewIdMappings;

            CatalogIdPatcher cip = new CatalogIdPatcher(catalogFileInfo, hardware2ProgramIdMapping, iPathETS, ns);
            cip.Patch();

            XmlSigning.SignDirectory(Path.Combine(outputFolder, manuId), iPathETS);
        }

        public static void ZipFolder(string outputFolder, string outputFile)
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(outputFolder, outputFile);
        }

        public static async Task CheckMaster(string outputFolder, int ns)
        {
            string basePath = Path.GetFullPath("..", outputFolder);
            if(File.Exists(Path.Combine(outputFolder, "knx_master.xml")))
            {
                string content = File.ReadAllText(Path.Combine(outputFolder, "knx_master.xml"));
                if(content.Contains($"http://knx.org/xml/project/{ns}"))
                {
                    //save it
                    if (!File.Exists(Path.Combine(basePath, "Masters", $"project-{ns}.xml")))
                    {
                        if(!Directory.Exists(Path.Combine(basePath, "Masters")))
                            Directory.CreateDirectory(Path.Combine(basePath, "Masters"));
                        File.Copy(Path.Combine(outputFolder, "knx_master.xml"), Path.Combine(basePath, "Masters", $"project-{ns}.xml"));
                    }
                    return;
                }
                File.Delete(Path.Combine(outputFolder, "knx_master.xml"));
            }

            if (!File.Exists(Path.Combine(basePath, "Masters", $"project-{ns}.xml")))
            {
                if(!Directory.Exists(Path.Combine(basePath, "Masters")))
                    Directory.CreateDirectory(Path.Combine(basePath, "Masters"));
                HttpClient client = new HttpClient();
                string masterXML = await client.GetStringAsync($"https://update.knx.org/data/XML/project-{ns}/knx_master.xml");
                File.WriteAllText(Path.Combine(basePath, "Masters", $"project-{ns}.xml"), masterXML);
            }
            File.Copy(Path.Combine(basePath, "Masters", $"project-{ns}.xml"), Path.Combine(outputFolder, $"knx_master.xml"));
        }

        private static void SplitXml(string lTempXmlFileName, string outputFolder)
        {
            //if (ValidateXsd(iWorkingDir, lTempXmlFileName, lTempXmlFileName, iXsdFileName, iAutoXsd)) return 1;

            Console.WriteLine("Generating knxprod file...");

            XDocument xdoc = null;
            string xmlContent = File.ReadAllText(lTempXmlFileName);
            xdoc = XDocument.Parse(xmlContent, LoadOptions.SetLineInfo);

            XNode lXmlModel = xdoc.FirstNode;
            if (lXmlModel.NodeType == XmlNodeType.ProcessingInstruction)
                lXmlModel.Remove();

            string ns = xdoc.Root.Name.NamespaceName;
            XElement xmanu = xdoc.Root.Element(XName.Get("ManufacturerData", ns)).Element(XName.Get("Manufacturer", ns));

            string manuId = xmanu.Attribute("RefId").Value;
            XElement xcata = xmanu.Element(XName.Get("Catalog", ns));
            XElement xhard = xmanu.Element(XName.Get("Hardware", ns));
            XElement xappl = xmanu.Element(XName.Get("ApplicationPrograms", ns));
            XElement xbagg = xmanu.Element(XName.Get("Baggages", ns));

            List<XElement> xcataL = new List<XElement>();
            List<XElement> xhardL = new List<XElement>();
            List<XElement> xapplL = new List<XElement>();
            List<XElement> xbaggL = new List<XElement>();
            XElement xlangs = xmanu.Element(XName.Get("Languages", ns));

            if (xlangs != null)
            {
                xlangs.Remove();
                foreach (XElement xTrans in xlangs.Descendants(XName.Get("TranslationUnit", ns)).ToList())
                {
                    DocumentCategory lCategory = GetDocumentCategory(xTrans);
                    switch (lCategory)
                    {
                        case DocumentCategory.Catalog:
                            AddTranslationUnit(xTrans, xcataL, ns);
                            break;
                        case DocumentCategory.Hardware:
                            AddTranslationUnit(xTrans, xhardL, ns);
                            break;
                        case DocumentCategory.Application:
                            AddTranslationUnit(xTrans, xapplL, ns);
                            break;
                        default:
                            throw new Exception("Unknown Translation Type: " + lCategory.ToString());
                    }

                }
            }
            xhard.Remove();
            if (xbagg != null) xbagg.Remove();

            //Save Catalog
            xappl.Remove();
            if (xcataL.Count > 0)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xcataL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            xdoc.Save(Path.Combine(outputFolder, manuId, "Catalog.xml"));
            if (xcataL.Count > 0) xlangs.Remove();
            xcata.Remove();

            // Save Hardware
            xmanu.Add(xhard);
            if (xhardL.Count > 0)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xhardL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            xdoc.Save(Path.Combine(outputFolder, manuId, "Hardware.xml"));
            if (xhardL.Count > 0) xlangs.Remove();
            xhard.Remove();

            if (xbagg != null)
            {
                // Save Baggages
                xmanu.Add(xbagg);
                if (xbaggL.Count > 0)
                {
                    xlangs.Elements().Remove();
                    foreach (XElement xlang in xbaggL)
                        xlangs.Add(xlang);
                    xmanu.Add(xlangs);
                }
                xdoc.Save(Path.Combine(outputFolder, manuId, "Baggages.xml"));
                if (xbaggL.Count > 0) xlangs.Remove();
                xbagg.Remove();
            }

            xmanu.Add(xappl);
            if (xapplL.Count > 0)
            {
                xlangs.Elements().Remove();
                foreach (XElement xlang in xapplL)
                    xlangs.Add(xlang);
                xmanu.Add(xlangs);
            }
            string appId = xappl.Elements(XName.Get("ApplicationProgram", ns)).First().Attribute("Id").Value;
            xdoc.Save(Path.Combine(outputFolder, manuId, $"{appId}.xml"));
            if (xapplL.Count > 0) xlangs.Remove();
        }

        private static void CopyBaggages(string iWorkingDir, string iBaggageName, string outputFolder, string manuId)
        {
            string lSourceBaggageName = Path.Combine(iWorkingDir, iBaggageName);
            var lSourceBaggageDir = new DirectoryInfo(lSourceBaggageName);
            Directory.CreateDirectory(Path.Combine(outputFolder, manuId, "Baggages"));
            if (lSourceBaggageDir.Exists)
                lSourceBaggageDir.DeepCopy(Path.Combine(outputFolder, manuId, "Baggages"));
        }

        private static DocumentCategory GetDocumentCategory(XElement iTranslationUnit)
        {
            DocumentCategory lCategory = DocumentCategory.None;
            string lId = iTranslationUnit.Attribute("RefId").Value;

            lId = lId.Substring(6);
            if (lId.StartsWith("_A-"))
                lCategory = DocumentCategory.Application;
            else if (lId.StartsWith("_CS-"))
                lCategory = DocumentCategory.Catalog;
            else if (lId.StartsWith("_H-") && lId.Contains("_CI-"))
                lCategory = DocumentCategory.Catalog;
            else if (lId.StartsWith("_H-") && lId.Contains("_P-"))
                lCategory = DocumentCategory.Hardware;
            else if (lId.StartsWith("_H-"))
                lCategory = DocumentCategory.Hardware;

            return lCategory;
        }
        
        private static string GetManuId(string lTempXmlFileName)
        {
            string content = File.ReadAllText(lTempXmlFileName);
            Regex regex = new Regex("<Manufacturer RefId=\"(M-[0-9A-F]{4})\">");
            Match m = regex.Match(content);
            if(m.Success)
                return m.Groups[1].Value;
            return "";
        }

        private static void AddTranslationUnit(XElement iTranslationUnit, List<XElement> iLanguageList, string iNamespaceName)
        {
            // we assume, that here are adding just few TranslationUnits
            // get parent element (Language)
            XElement lSourceLanguage = iTranslationUnit.Parent;
            string lSourceLanguageId = lSourceLanguage.Attribute("Identifier").Value;
            XElement lTargetLanguage = iLanguageList.Elements("Child").FirstOrDefault(child => child.Attribute("Name").Value == lSourceLanguageId);
            if (lTargetLanguage == null)
            {
                // we create language element
                lTargetLanguage = new XElement(XName.Get("Language", iNamespaceName), new XAttribute("Identifier", lSourceLanguageId));
                iLanguageList.Add(lTargetLanguage);
            }
            iTranslationUnit.Remove();
            lTargetLanguage.Add(iTranslationUnit);
        }
    
        //installation path of a valid ETS instance (only ETS5 or ETS6 supported)
        private static List<string> gPathETS = new List<string> {
            @"C:\Program Files (x86)\ETS6",
            @"C:\Program Files\ETS6",
            @"C:\Program Files (x86)\ETS5",
            @"C:\Program Files\ETS5"
        };

        private static List<EtsVersion> etsVersions = new() {
            new EtsVersion("6.2", 24, false),
            new EtsVersion("6.1", 23, false),
            new EtsVersion("6.0", 22, false),
            new EtsVersion("5.7", 20, true),
            new EtsVersion("5.6", 14, true),
            new EtsVersion("5.1", 13, true),
            new EtsVersion("5.0", 12, true),
            new EtsVersion("4.0", 11, true),
        };

#nullable enable
        private static EtsVersion? checkEtsPath(string path, int ns)
        {
            if(!File.Exists(System.IO.Path.Combine(path, "Knx.Ets.Xml.ObjectModel.dll"))) return null;
                string versionInfo = FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(path, "Knx.Ets.Xml.ObjectModel.dll")).FileVersion?.Substring(0,3) ?? "0.0";
            
            EtsVersion? vers = etsVersions.SingleOrDefault(v => v.Version == versionInfo);

            if(vers != null && vers.CheckNs(ns)) return vers;
            return null;
        }

        public static string FindEtsPath(int namespaceVersion, bool silent = false)
        {
            if(Directory.Exists(System.IO.Path.Combine(Directory.GetCurrentDirectory(), "CV"))) {
                foreach(string path in Directory.GetDirectories(System.IO.Path.Combine(Directory.GetCurrentDirectory(), "CV")).Reverse()) {
                    EtsVersion? ets = checkEtsPath(path, namespaceVersion);
                    if(ets != null) {
                        if(!silent)
                            Console.WriteLine($"Found namespace {namespaceVersion} in xml, using ETS {ets.Version} (local) for conversion... (Path: {path})");
                        return path;
                    }
                }
            }

            foreach(string path in gPathETS)
            {
                EtsVersion? ets = checkEtsPath(path, namespaceVersion);
                if(ets != null) {
                    if(!silent)
                        Console.WriteLine($"Found namespace {namespaceVersion} in xml, using ETS {ets.Version} for conversion... (Path: {path})");
                    return path;
                }
            }

            return "";
        }
    }
}
#nullable disable