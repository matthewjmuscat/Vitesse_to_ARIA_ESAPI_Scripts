using System; // Provides basic system functions like input/output, exceptions, etc.
using System.Linq; // Provides LINQ (Language Integrated Query) functionality for working with collections.
using System.Collections.Generic; // Provides generic collections such as List, Dictionary, etc.
using System.IO; // Provides file and directory manipulation functions.
using EvilDICOM.Core; // Provides core DICOM functionalities from the EvilDICOM library.
using EvilDICOM.Network; // Provides network communication capabilities for DICOM services.
using EvilDICOM.Network.Enums; // Provides the status codes for C-STORE.
using EvilDICOM.Network.SCUOps; // Provides SCU operations like C-STORE.
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_Patient = VMS.TPS.Common.Model.API.Patient;

// Alias to resolve ambiguity
using EsapiApplication = VMS.TPS.Common.Model.API.Application;
using EvilDICOM.Core.Modules;
using System.Xml;
using System.Reflection;
[assembly: ESAPIScript(IsWriteable = true)]  // Add this line to allow modifications

/*
// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]
*/


// IMPORTANT NOTES:
// 1. ENSURE TO SPECIFY THE START AND END INDEX! THIS CONTROLS THE BATCH YOU WILL DO IN THE FOLDER ARCHIVE!
// 2. ENSURE THAT THE ARCHIVE FOLDER IS CORRECTLY SPECIFIED!
// 3. ENSURE THAT THE ARIA DAEMON IP IS CORRECTLY SPECIFIED!
// 4. ENSURE THAT THE MODIFIED FRACTION FOLDER NAMES ARE CORRECTLY SPECIFIED!


namespace VMS.TPS
{
    class Program
    {

        private static string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");

        /* Config file should look something like this, if you don't have one, create a file called config.xml and place it in the same directory as the executable.
        <?xml version="1.0" encoding="utf-8"?>
        <Configuration>
            <StoragePath>H:\CCSI\PlanningModule\Physics Patient QA\VitesseBackup\Vitesse DICOM Archive1</StoragePath>
            <VitesseBackupFolderName>Vitesse Backup</VitesseBackupFolderName>
            <SelectionMode>Indices</SelectionMode>  <!-- Options: "Range" or "Indices" -->
            <StartIndex>8</StartIndex>
            <EndIndex>8</EndIndex>
            <Indices>155</Indices> <!-- Only used if SelectionMode is "Indices" -->
        </Configuration>
         */

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                using (EsapiApplication app = EsapiApplication.CreateApplication())
                {
                    Console.WriteLine("ARIA connection initialized successfully.");
                    Execute(app);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error during initialization: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("Inner Exception: " + ex.InnerException.Message);
                }
                Console.Error.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("End of Main(). Waiting for Enter:");
            Console.ReadLine();

        }

        static void Execute(EsapiApplication app)
        {
            string storagePath;
            string vitesseBackupFolderName;
            List<int> selectedIndices = ReadConfigFromXML(out storagePath, out vitesseBackupFolderName);

            if (selectedIndices.Count == 0)
            {
                Console.WriteLine("No valid patient indices provided.");
                return;
            }


            if (!Directory.Exists(storagePath))
            {
                Console.WriteLine($"Error: Storage path does not exist: {storagePath}");
                return;
            }

            // Initialize a message ID for DICOM C-STORE operations.
            ushort msgId = 1;

            // Define the remote DICOM entity (daemon) to communicate with, using AE Title, IP address, and port.
            var daemon = new Entity("MyDaemon", "10.2.98.5", 51402);

            // Create the local DICOM entity (client) using the machine's name and a specified port.
            var local = Entity.CreateLocal(Environment.MachineName, 9999);

            // Output local AE (Application Entity) details to the console for verification.
            Console.WriteLine("Local AE: " + local.AeTitle + "\nLocal IP: " + local.IpAddress + "\nLocal Port: " + local.Port);

            // Create a DICOM Service Class User (SCU) object to handle DICOM operations from the local entity.
            var client = new DICOMSCU(local);

            // Get a C-STORE object from the client to send DICOM data to the daemon (remote entity).
            var storer = client.GetCStorer(daemon);

            // Get all patient folders, sorted alphabetically
            var patientFolders = Directory.GetDirectories(storagePath).OrderBy(f => Path.GetFileName(f)).ToList();

            /*
            // User-defined indices
            string patientIndicesInput = "197"; // Provide a comma-separated list like "1,3,5,7", if empty will use start and end indices
            int startIndex = 8; // Change to starting index
            int endIndex = 8;   // Change to ending index

            // Determine indices to process
            List<int> selectedIndices = new List<int>();

            if (!string.IsNullOrWhiteSpace(patientIndicesInput))
            {
                selectedIndices = patientIndicesInput.Split(',')
                    .Select(s => int.TryParse(s.Trim(), out int num) ? num : -1)
                    .Where(num => num > 0 && num <= patientFolders.Count)
                    .Select(num => num - 1)
                    .ToList();
            }
            else
            {
                selectedIndices = Enumerable.Range(startIndex - 1, endIndex - startIndex + 1)
                    .Where(i => i < patientFolders.Count)
                    .ToList();
            }
            */

            if (patientFolders.Count == 0)
            {
                Console.WriteLine("No patient folders found in the specified storage path.");
                return;
            }

            // Ensure selected indices do not exceed available patient folders
            selectedIndices = selectedIndices.Where(i => i < patientFolders.Count).ToList();

            if (selectedIndices.Count == 0)
            {
                Console.WriteLine("No valid patient indices after filtering out out-of-bounds indices.");
                return;
            }


            // Determine the first and last patient names to include in the global log file name
            string firstPatientName = Path.GetFileName(patientFolders[selectedIndices.First()]);
            string lastPatientName = Path.GetFileName(patientFolders[selectedIndices.Last()]);

            // Update the global log file name to include the first and last patient names analyzed
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string globalLogFileName = $"global_summary_and_failures_log - {firstPatientName}_to_{lastPatientName} - {timestamp}.txt";
            string globalLogPath = Path.Combine(storagePath, globalLogFileName);


            using (StreamWriter globalLog = new StreamWriter(globalLogPath))
            {
                // Process only the patients within the specified range
                foreach (var i in selectedIndices)
                {
                    var patientFolder = patientFolders[i];

                    // Write patient name to the global log
                    globalLog.WriteLine($"Patient: {Path.GetFileName(patientFolder)}");

                    // List to track successfully sent file names
                    List<string> successfulFiles = new List<string>();

                    // For each patient folder, get fraction subfolders
                    var fractionFolders = Directory.GetDirectories(patientFolder);
                    foreach (var fractionFolder in fractionFolders)
                    {
                        // Only process files in the "Edited for Eclipse Import" folder
                        var importFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import");
                        if (Directory.Exists(importFolderPath))
                        {
                            // Get DICOM files from the "Edited for Eclipse Import" folder
                            var dcmFiles = Directory.GetFiles(importFolderPath);

                            // Separate the files into non-plan/dose files, plan files (starting with "PL"), and dose files (starting with "DO")
                            var nonPlanDoseFiles = dcmFiles.Where(f => !Path.GetFileName(f).StartsWith("PL") && !Path.GetFileName(f).StartsWith("DO")).ToList();
                            var planFiles = dcmFiles.Where(f => Path.GetFileName(f).StartsWith("PL")).ToList();
                            var doseFiles = dcmFiles.Where(f => Path.GetFileName(f).StartsWith("DO")).ToList();

                            // Create a log for the current fraction folder
                            string fractionLogPath = Path.Combine(fractionFolder, "cstore_log.txt");
                            using (StreamWriter fractionLog = new StreamWriter(fractionLogPath))
                            {
                                
                                // Send all non-plan and non-dose files
                                //SendFiles(nonPlanDoseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);

                                // Send all plan files
                                //SendFiles(planFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);

                                // Send all dose files
                                //SendFiles(doseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);

                                string patientId = (DICOMObject.Read(dcmFiles.First()).FindFirst("00100020") as EvilDICOM.Core.Element.LongString)?.Data;

                                // Create a Vitesse Backup course if it does not exist
                                if (!string.IsNullOrEmpty(patientId))
                                {
                                    EnsureVitesseBackupCourseExists(app, patientId, globalLog, vitesseBackupFolderName);
                                }

                                // Separate flags for each file group
                                bool nonPlanDoseError272 = false;
                                bool planFilesError272 = false;
                                bool doseFilesError272 = false;

                                // Call SendFilesAndTryToModify for each file group
                                nonPlanDoseError272 = SendFilesAndTryToModify(nonPlanDoseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);
                                planFilesError272 = SendFilesAndTryToModify(planFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);
                                doseFilesError272 = SendFilesAndTryToModify(doseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, fractionFolder, successfulFiles, i);

                                // --- Resend Block (only if error 272 was found) ---
                                // Check if any error 272 occurred
                                if (nonPlanDoseError272 || planFilesError272 || doseFilesError272)
                                {
                                    Console.WriteLine($"272 error(s) detected. Non-plan272: {nonPlanDoseError272}, plan272: {planFilesError272}, dose272: {doseFilesError272}. Initiating resend block...");
                                    globalLog.WriteLine($"272 error(s) detected. Non-plan272: {nonPlanDoseError272}, plan272: {planFilesError272}, dose272: {doseFilesError272}. Initiating resend block...");

                                    // Create a new folder for the resend attempt
                                    var resendFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import - 272 Resend");
                                    if (!Directory.Exists(resendFolderPath))
                                    {
                                        Directory.CreateDirectory(resendFolderPath);
                                    }

                                    // Copy all files from the original folder into the resend folder
                                    foreach (var file in dcmFiles)
                                    {
                                        string destFile = Path.Combine(resendFolderPath, Path.GetFileName(file));
                                        File.Copy(file, destFile, true);
                                    }

                                    // Retrieve the first DICOM file's patient ID from the original folder
                                    //string patientId = (DICOMObject.Read(dcmFiles.First()).FindFirst("00100020") as EvilDICOM.Core.Element.LongString)?.Data;
                                    var patient = app.OpenPatientById(patientId);

                                    if (patient != null)
                                    {
                                        // Retrieve correct patient information from Aria
                                        string correctFirstName = patient.FirstName;
                                        string correctLastName = patient.LastName;
                                        string correctBirthDate = patient.DateOfBirth?.ToString("yyyyMMdd") ?? "";


                                        Console.WriteLine($"Retrieved from Aria - Corrected Patient Info: FN - '{correctFirstName}', LN - '{correctLastName}', DOB: '{correctBirthDate}'");

                                        // Modify copied DICOM files
                                        var resendDcmFiles = Directory.GetFiles(resendFolderPath).ToList();
                                        foreach (var file in resendDcmFiles)
                                        {
                                            var modifiedDcm = DICOMObject.Read(file);
                                            modifiedDcm.ReplaceOrAdd(DICOMForge.PatientName($"{correctLastName}^{correctFirstName}"));
                                            if (!string.IsNullOrEmpty(correctBirthDate) && DateTime.TryParseExact(correctBirthDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime parsedBirthDate))
                                            {
                                                modifiedDcm.ReplaceOrAdd(DICOMForge.PatientBirthDate(parsedBirthDate));
                                            }
                                            modifiedDcm.Write(file); // Overwrite the copied DICOM file
                                        }

                                        // Separate the files into non-plan/dose files, plan files (starting with "PL"), and dose files (starting with "DO")
                                        var resendnonPlanDoseFiles = resendDcmFiles.Where(f => !Path.GetFileName(f).StartsWith("PL") && !Path.GetFileName(f).StartsWith("DO")).ToList();
                                        var resendplanFiles = resendDcmFiles.Where(f => Path.GetFileName(f).StartsWith("PL")).ToList();
                                        var resenddoseFiles = resendDcmFiles.Where(f => Path.GetFileName(f).StartsWith("DO")).ToList();

                                        // Resend modified files (second and final attempt)
                                        // Separate flags for each file group
                                        bool resendnonPlanDoseError272 = false;
                                        bool resendplanFilesError272 = false;
                                        bool resenddoseFilesError272 = false;

                                        resendnonPlanDoseError272 = SendFilesAndTryToModify(resendnonPlanDoseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, resendFolderPath, successfulFiles, i);
                                        resendplanFilesError272 = SendFilesAndTryToModify(resendplanFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, resendFolderPath, successfulFiles, i);
                                        resenddoseFilesError272 = SendFilesAndTryToModify(resenddoseFiles, storer, local, daemon, ref msgId, fractionLog, globalLog, patientFolder, resendFolderPath, successfulFiles, i);

                                        // Check if any error 272 occurred
                                        if (resendnonPlanDoseError272 || resendplanFilesError272 || resenddoseFilesError272)
                                        {
                                            Console.WriteLine($"Resend attempt still produced 272 errors. Non-plan272: {resendnonPlanDoseError272}, plan272: {resendplanFilesError272}, dose272: {resenddoseFilesError272}. No further attempts will be made.");
                                            globalLog.WriteLine($"Resend attempt still produced 272 errors. Non-plan272: {resendnonPlanDoseError272}, plan272: {resendplanFilesError272}, dose272: {resenddoseFilesError272}. No further attempts will be made.");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"Patient {patientId} not found in Aria. Skipping 272 resend.");
                                        globalLog.WriteLine($"Patient {patientId} not found in Aria. Skipping 272 resend.");
                                    }

                                    app.ClosePatient();
                                }
                                // --- End of Resend Block ---

                            }
                        }
                    }

                    // After processing the patient, add separator to the global log
                    globalLog.WriteLine("___"); // Add separator between patients

                    // Generate summary of successfully sent files for this patient
                    var fileSummary = successfulFiles
                        .GroupBy(f => f.Substring(0, 2)) // Group by the first two letters of the file name
                        .Select(g => $"{g.Count()} {g.Key} files sent") // Create summary string
                        .ToList();

                    // Write summary to the global log
                    globalLog.WriteLine("Summary of successfully sent files:");
                    foreach (var summary in fileSummary)
                    {
                        globalLog.WriteLine(summary);
                    }

                    // Add another separator after the summary
                    globalLog.WriteLine("___");
                }

                // Completion message
                Console.WriteLine("All patients executed.");

            }           
        }

        // Method to send DICOM files, log results to both console and files
        public static void SendFiles(List<string> files, CStorer storer, Entity local, Entity daemon, ref ushort msgId, StreamWriter fractionLog, StreamWriter globalLog, string patientFolder, string fractionFolder, List<string> successfulFiles, int i)
        {
            foreach (var path in files)
            {
                try
                {
                    // Read the DICOM file into memory as a DICOMObject.
                    var dcm = DICOMObject.Read(path);

                    // Send the DICOM object to the daemon using C-STORE.
                    var response = storer.SendCStore(dcm, ref msgId);

                    // Get the name of the status (e.g., "SUCCESS")
                    string statusName = Enum.GetName(typeof(Status), response.Status);

                    // Create log entry and print it to the console
                    string logEntry = $"{i+1} Patient: {Path.GetFileName(patientFolder)}, Fraction: {Path.GetFileName(fractionFolder)}, DICOM C-Store from {local.AeTitle} => {daemon.AeTitle}@{daemon.IpAddress}:{daemon.Port} for file {Path.GetFileName(path)}: {statusName} (Code: {response.Status})";
                    Console.WriteLine(logEntry); // Output to the console
                    fractionLog.WriteLine(logEntry); // Write to fraction log

                    // If the status is "SUCCESS", add the file name to the successfulFiles list
                    if (statusName == "SUCCESS")
                    {
                        successfulFiles.Add(Path.GetFileName(path));
                    }
                    else
                    {
                        // Log failure in the global log
                        string failureLogEntry = $"Fraction: {Path.GetFileName(fractionFolder)}, File: {Path.GetFileName(path)}, Status: {statusName}";
                        globalLog.WriteLine(failureLogEntry);
                    }
                }
                catch (Exception ex)
                {
                    // Handle and log any errors
                    string errorLogEntry = $"Patient: {Path.GetFileName(patientFolder)}, Fraction: {Path.GetFileName(fractionFolder)}, Error processing file {Path.GetFileName(path)}: {ex.Message}";
                    Console.WriteLine(errorLogEntry); // Output to the console
                    fractionLog.WriteLine(errorLogEntry); // Write to fraction log
                    globalLog.WriteLine(errorLogEntry); // Write to global log
                }
            }
        }


        public static bool SendFilesAndTryToModify(
                                     List<string> files,
                                     CStorer storer,
                                     Entity local,
                                     Entity daemon,
                                     ref ushort msgId,
                                     StreamWriter fractionLog,
                                     StreamWriter globalLog,
                                     string patientFolder,
                                     string fractionFolder,
                                     List<string> successfulFiles,
                                     int i)

        {
            bool encountered272 = false; // Track if any file resulted in error 272

            foreach (var path in files)
            {
                try
                {
                    // Read the DICOM file into memory
                    var dcm = DICOMObject.Read(path);

                    // Send the original DICOM
                    var response = storer.SendCStore(dcm, ref msgId);

                    // Log initial send attempt
                    string logEntry = $"{i + 1} Patient: {Path.GetFileName(patientFolder)}, Fraction: {Path.GetFileName(fractionFolder)}, " +
                                      $"DICOM C-Store from {local.AeTitle} => {daemon.AeTitle}@{daemon.IpAddress}:{daemon.Port} " +
                                      $"for file {Path.GetFileName(path)}: Code {response.Status}";
                    Console.WriteLine(logEntry);
                    fractionLog.WriteLine(logEntry);

                    // Check for success
                    if (response.Status == (ushort)Status.SUCCESS)
                    {
                        successfulFiles.Add(Path.GetFileName(path));
                    }
                    // Check for error 272 (Patient ID does not match)
                    else if (response.Status == 272) // OR use: else if (response.Status == (ushort)Status.PatientIdDoesNotMatch) if available
                    {
                        string error272Log = $"Error 272 encountered for {Path.GetFileName(path)}.";
                        Console.WriteLine(error272Log);
                        globalLog.WriteLine(error272Log);
                        fractionLog.WriteLine(error272Log);
                        encountered272 = true; // Flag for error 272
                    }
                    else
                    {
                        // Log any other failures
                        string failureLogEntry = $"Failure: {Path.GetFileName(path)} in Fraction {Path.GetFileName(fractionFolder)} - Code {response.Status}";
                        Console.WriteLine(failureLogEntry);
                        globalLog.WriteLine(failureLogEntry);
                        fractionLog.WriteLine(failureLogEntry);
                    }
                }
                catch (Exception ex)
                {
                    string errorLogEntry = $"Exception: {Path.GetFileName(path)} in Fraction {Path.GetFileName(fractionFolder)} - {ex.Message}";
                    Console.WriteLine(errorLogEntry);
                    fractionLog.WriteLine(errorLogEntry);
                    globalLog.WriteLine(errorLogEntry);
                }
            }

            return encountered272; // Return flag indicating if error 272 was encountered
        }

        static void EnsureVitesseBackupCourseExists(EsapiApplication app, string patientId, StreamWriter logWriter, string vitesseBackupFolderName)
        {
            ESAPI_Patient patient = null;

            try
            {
                // Open the patient in ARIA
                patient = app.OpenPatientById(patientId);
                if (patient == null)
                {
                    logWriter.WriteLine($"Patient {patientId} not found in ARIA. Cannot create {vitesseBackupFolderName} course.");
                    Console.WriteLine($"Patient {patientId} not found in ARIA. Cannot create {vitesseBackupFolderName} course.");
                    return;
                }

                // Check if the course already exists
                if (patient.Courses.Any(c => c.Id == vitesseBackupFolderName))
                {
                    logWriter.WriteLine($"Course '{vitesseBackupFolderName}' already exists for patient {patientId}. No need to create.");
                    Console.WriteLine($"Course '{vitesseBackupFolderName}' already exists for patient {patientId}. No need to create.");
                }
                else
                {
                    // Create the course
                    patient.BeginModifications();
                    Course vitesseBackupCourse = patient.AddCourse();
                    vitesseBackupCourse.Id = vitesseBackupFolderName;
                    // Save changes
                    app.SaveModifications();

                    logWriter.WriteLine($"Created new course '{vitesseBackupFolderName}' for patient {patientId}.");
                    Console.WriteLine($"Created new course '{vitesseBackupFolderName}' for patient {patientId}.");
                    
                }
            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"Error creating '{vitesseBackupFolderName}' course for patient {patientId}: {ex.Message}");
                Console.WriteLine($"Error creating '{vitesseBackupFolderName}' course for patient {patientId}: {ex.Message}");
            }
            finally
            {
                if (patient != null)
                {
                    app.ClosePatient();
                }
            }
        }



        static List<int> ReadConfigFromXML(out string storagePath, out string vitesseBackupFolderName)
        {
            List<int> indices = new List<int>();
            storagePath = "";
            vitesseBackupFolderName = "Vitesse Backup"; // Default name if not specified in XML


            if (!File.Exists(configFilePath))
            {
                Console.WriteLine($"Config file not found: {configFilePath}");
                Console.WriteLine("Using default settings.");
                return indices;
            }

            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(configFilePath);

                // Read StoragePath
                storagePath = xmlDoc.SelectSingleNode("//StoragePath")?.InnerText.Trim();
                if (string.IsNullOrEmpty(storagePath) || !Directory.Exists(storagePath))
                {
                    Console.WriteLine("Error: Invalid or non-existent StoragePath in config.xml.");
                    return indices;
                }

                // Read VitesseBackupFolderName
                vitesseBackupFolderName = xmlDoc.SelectSingleNode("//VitesseBackupFolderName")?.InnerText.Trim() ?? "Vitesse Backup";

                var patientFolders = Directory.GetDirectories(storagePath).OrderBy(f => Path.GetFileName(f)).ToList();
                int patientCount = patientFolders.Count;

                if (patientCount == 0)
                {
                    Console.WriteLine("No patient folders found in the specified StoragePath.");
                    return indices;
                }

                // Read SelectionMode (Range or Indices)
                string selectionMode = xmlDoc.SelectSingleNode("//SelectionMode")?.InnerText.Trim();
                if (selectionMode == "Range")
                {
                    int startIndex = int.Parse(xmlDoc.SelectSingleNode("//StartIndex")?.InnerText ?? "1");
                    int endIndex = int.Parse(xmlDoc.SelectSingleNode("//EndIndex")?.InnerText ?? patientCount.ToString());

                    startIndex = Math.Max(1, startIndex);
                    endIndex = Math.Min(patientCount, endIndex);

                    indices = Enumerable.Range(startIndex - 1, endIndex - startIndex + 1)
                        .Where(i => i < patientCount)
                        .ToList();
                }
                else if (selectionMode == "Indices")
                {
                    string indicesInput = xmlDoc.SelectSingleNode("//Indices")?.InnerText ?? "";
                    indices = indicesInput.Split(',')
                        .Select(s => int.TryParse(s.Trim(), out int num) ? num : -1)
                        .Where(num => num > 0 && num <= patientCount)
                        .Select(num => num - 1)
                        .ToList();
                }
                else
                {
                    Console.WriteLine("Invalid SelectionMode in config. Use 'Range' or 'Indices'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading config: {ex.Message}");
            }

            return indices;
        }


    }
}
