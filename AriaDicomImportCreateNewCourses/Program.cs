using System;
using System.IO;
using System.Linq;
using System.Xml;
using VMS.TPS.Common.Model.API;
using EvilDICOM.Core;
using System.Collections.Generic;
using EvilDICOM.Network.Enums;
using EvilDICOM.Network;
using EvilDICOM.Core.Modules;
using ESAPI_Patient = VMS.TPS.Common.Model.API.Patient;
using System.Numerics;
using VMS.TPS.Common.Model.Types;
using System.Reflection;



/*
// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]
*/
[assembly: ESAPIScript(IsWriteable = true)]  // Add this line to allow modifications


/* 
This code is a standalone script that attempts to create a new Course, and move dicoms that match a certain UID that are found in a well structured directory into the new course, and remove them from the old course.

*** Note: Feb 7 2025 - Unfortunately the CopyPlanSetup method (a method under course object) works fine for external beam plans, but not brachytherpay plans, which is what this code was created for...
*/
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

        private static bool renameCourseFoldertoVitesseBackup = true; // Toggle to enable/disable renaming course folder to Vitesse Backup

        private static bool removePlansFromIncorrectCourses = false; // Toggle to enable/disable removing plans from incorrect courses

        private static bool moveDICOMtoVitesseBackup = false; // Toggle to enable/disable moving DICOM files to Vitesse Backup 

        private static bool removeOriginalPlan = false; // Toggle to enable/disable removal of original plan, only relevant if moveDICOMtoVitesseBackup is true

        private static bool sendDICOMFiles = false; // Toggle to enable/disable sending DICOM files via C-STORE 

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                using (Application app = Application.CreateApplication())
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
            Console.WriteLine("Waiting for Enter:");
            Console.ReadLine();
        }

        static void Execute(Application app)
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

            var patientFolders = Directory.GetDirectories(storagePath).OrderBy(f => Path.GetFileName(f)).ToList();

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

            /*
            var storagePath = @"H:\\CCSI\\PlanningModule\\Physics Patient QA\\VitesseBackup\\Vitesse DICOM Archive1";
            var patientFolders = Directory.GetDirectories(storagePath).OrderBy(f => Path.GetFileName(f)).ToList();

            // User-defined indices
            string patientIndicesInput = "155"; // Provide a comma-separated list like "1,3,5,7", if empty will use start and end indices
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
                // Ensure indices are within bounds
                startIndex = Math.Max(0, startIndex);
                endIndex = Math.Min(endIndex, patientFolders.Count - 1);

                selectedIndices = Enumerable.Range(startIndex - 1, endIndex - startIndex + 1)
                    .Where(i => i < patientFolders.Count)
                    .ToList();
            }

            if (selectedIndices.Count == 0)
            {
                Console.WriteLine("No valid patient indices provided.");
                return;
            }
            */

            // Determine the first and last patient names to include in the global log file name
            string firstPatientName = Path.GetFileName(patientFolders[selectedIndices.First()]);
            string lastPatientName = Path.GetFileName(patientFolders[selectedIndices.Last()]);

            // Generate log file name with timestamp and indices
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logFilePath = Path.Combine(storagePath, $"dicom_transfer_log_{timestamp}_indices_{firstPatientName}_to_{lastPatientName}.txt");

            using (StreamWriter logWriter = new StreamWriter(logFilePath))
            {
                logWriter.WriteLine($"Processing patient folders from {firstPatientName} to {lastPatientName}");
                Console.WriteLine($"Processing patient folders from {firstPatientName} to {lastPatientName}");

                foreach (var i in selectedIndices)
                {
                    string patientFolder = patientFolders[i];
                    string patientName = Path.GetFileName(patientFolder);
                    logWriter.WriteLine($"Processing Patient Folder: {patientName}");
                    Console.WriteLine($"Processing Patient Folder: {patientName}");


                    List<string> sopInstanceUIDs = new List<string>();

                    string patientId = string.Empty; // Initialized as an empty string

                    var fractionFolders = Directory.GetDirectories(patientFolder);
                    foreach (var fractionFolder in fractionFolders)
                    {
                        string fractionFoldername = Path.GetFileName(fractionFolder);
                        logWriter.WriteLine($"Processing Fraction Folder: {fractionFoldername}");
                        Console.WriteLine($"Processing Fraction Folder: {fractionFoldername}");

                        var importFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import");
                        if (Directory.Exists(importFolderPath))
                        {
                            var planFilePath = Path.Combine(importFolderPath, "PL001.dcm");
                            if (File.Exists(planFilePath))
                            {
                                logWriter.WriteLine($"Processing Fraction Folder: {planFilePath}");
                                Console.WriteLine($"Processing Fraction Folder: {planFilePath}");
                                try
                                {
                                    var dcm = DICOMObject.Read(planFilePath);
                                    var patientIDElement = dcm.FindFirst("00100020") as EvilDICOM.Core.Element.LongString;
                                    var studyInstanceUIDElement = dcm.FindFirst("0020000D") as EvilDICOM.Core.Element.UniqueIdentifier; // study Instance UID GROUPS dicoms of the same study together, may be useful for moving SS, DOSE and Images together after creating the new course under the correct plan!
                                    var sopInstanceUIDElement = dcm.FindFirst("00080018") as EvilDICOM.Core.Element.UniqueIdentifier; // SOP Instance UID is instance UID of plan file


                                    if (patientIDElement != null && studyInstanceUIDElement != null && sopInstanceUIDElement != null)
                                    {
                                        patientId = patientIDElement.Data;
                                        var studyInstanceUID = studyInstanceUIDElement.Data;
                                        var sopInstanceUID = sopInstanceUIDElement.Data;

                                        sopInstanceUIDs.Add(sopInstanceUID);

                                        logWriter.WriteLine($"Found Patient ID: {patientId}, Study Instance UID: {studyInstanceUID}, SOP Instance UID: {sopInstanceUID}");
                                        Console.WriteLine($"Found Patient ID: {patientId}, Study Instance UID: {studyInstanceUID}, SOP Instance UID: {sopInstanceUID}");

                                        if (moveDICOMtoVitesseBackup)
                                        {
                                            MoveDICOMToVitesseBackup(app, patientId, sopInstanceUID, logWriter, removeOriginalPlan, vitesseBackupFolderName);
                                        }
                                        
                                        if (sendDICOMFiles)
                                        {
                                            SendDICOMFilesToAria(patientFolder, fractionFolder, logWriter);
                                        }
                                    }
                                    else
                                    {
                                        logWriter.WriteLine("Patient ID or Study Instance UID not found in plan file.");
                                        Console.WriteLine("Patient ID or Study Instance UID not found in plan file.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logWriter.WriteLine($"Error processing {planFilePath}: {ex.Message}");
                                    Console.WriteLine($"Error processing {planFilePath}: {ex.Message}");
                                }
                            }
                            else
                            {
                                logWriter.WriteLine("Plan file PL001.dcm not found in folder.");
                                Console.WriteLine("Plan file PL001.dcm not found in folder.");
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(patientId) && renameCourseFoldertoVitesseBackup == true)
                    {
                        RenameCourseWithCorrectPlansAndRemovePlansFromIncorrectCourses(app, patientId, sopInstanceUIDs, logWriter, vitesseBackupFolderName, removePlansFromIncorrectCourses);
                    }
                    else
                    {
                        logWriter.WriteLine($"Skipping course renaming for {patientName} because no valid Patient ID was found from the plan files.");
                        Console.WriteLine($"Skipping course renaming for {patientName} because no valid Patient ID was found from the plan files.");
                    }

                    logWriter.WriteLine("___");
                }
            }
        }

        static void SendDICOMFilesToAria(string patientFolder, string fractionFolder, StreamWriter logWriter)
        {
            var importFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import");
            if (Directory.Exists(importFolderPath))
            {
                var dcmFiles = Directory.GetFiles(importFolderPath);
                var daemon = new Entity("MyDaemon", "10.2.98.5", 51402);
                var local = Entity.CreateLocal(Environment.MachineName, 9999);
                var client = new DICOMSCU(local);
                var storer = client.GetCStorer(daemon);
                ushort msgId = 1;

                foreach (var path in dcmFiles)
                {
                    try
                    {
                        var dcm = DICOMObject.Read(path);
                        var response = storer.SendCStore(dcm, ref msgId);
                        string statusName = Enum.GetName(typeof(Status), response.Status);
                        logWriter.WriteLine($"C-STORE for {Path.GetFileName(path)}: {statusName}");
                    }
                    catch (Exception ex)
                    {
                        logWriter.WriteLine($"Error sending {Path.GetFileName(path)} via C-STORE: {ex.Message}");
                    }
                }
            }
        }

        static void MoveDICOMToVitesseBackup(Application app, string patientId, string sopInstanceUID, StreamWriter logWriter, bool removeOriginalPlan, string vitesseBackupFolderName)
        {
            ESAPI_Patient patient = null;


            try
            {
                patient = app.OpenPatientById(patientId);
                if (patient == null)
                {
                    logWriter.WriteLine("Patient not found in ARIA.");
                    Console.WriteLine("Patient not found in ARIA.");
                    return;
                }
                patient.BeginModifications();

                Course vitesseBackupCourse;
                if (patient.Courses.Any(c => c.Id == vitesseBackupFolderName))
                {
                    vitesseBackupCourse = patient.Courses.Single(c => c.Id == vitesseBackupFolderName);
                }
                else
                {
                    vitesseBackupCourse = patient.AddCourse();
                    vitesseBackupCourse.Id = vitesseBackupFolderName;
                    logWriter.WriteLine($"Created new course: {vitesseBackupFolderName}");
                    Console.WriteLine($"Created new course: {vitesseBackupFolderName}");
                }



                // Log all UIDs found before filtering
                var allPlanSetups = patient.Courses.SelectMany(c => c.PlanSetups).ToList();

                if (allPlanSetups.Count > 0)
                {
                    logWriter.WriteLine($"Total Plans Found: {allPlanSetups.Count}");
                    Console.WriteLine($"Total Plans Found: {allPlanSetups.Count}");

                    foreach (var plan in allPlanSetups)
                    {
                        logWriter.WriteLine($"  - Plan ID: {plan.Id}, Course: {plan.Course.Id}, UID: {plan.UID}");
                        Console.WriteLine($"  - Plan ID: {plan.Id}, Course: {plan.Course.Id}, UID: {plan.UID}");
                    }
                }
                else
                {
                    logWriter.WriteLine("No plans found in any course.");
                    Console.WriteLine("No plans found in any course.");
                }

                // Now, filter the plans that match the sopInstanceUID
                var sopPlans = allPlanSetups.Where(p => p.UID == sopInstanceUID).ToList();

                // Log the filtered plans
                if (sopPlans.Count > 0)
                {
                    logWriter.WriteLine($"Found {sopPlans.Count} plan(s) with SOP Instance UID: {sopInstanceUID}");
                    Console.WriteLine($"Found {sopPlans.Count} plan(s) with SOP Instance UID: {sopInstanceUID}");

                    foreach (var plan in sopPlans)
                    {
                        logWriter.WriteLine($"  - Plan ID: {plan.Id}, Course: {plan.Course.Id}, UID: {plan.UID}");
                        Console.WriteLine($"  - Plan ID: {plan.Id}, Course: {plan.Course.Id}, UID: {plan.UID}");
                    }
                }
                else
                {
                    logWriter.WriteLine($"No plans found matching SOP Instance UID: {sopInstanceUID}");
                    Console.WriteLine($"No plans found matching SOP Instance UID: {sopInstanceUID}");
                }


                // Process each matching plan
                foreach (var plan in sopPlans)
                {
                    logWriter.WriteLine($"Processing {plan.Id}");
                    Console.WriteLine($"Processing {plan.Id}");

                    // Find all courses containing the plan with the same UID, excluding vitesseBackupFolderName
                    var originalCourses = patient.Courses
                        .Where(c => c.PlanSetups.Any(p => p.UID == plan.UID) && c.Id != vitesseBackupFolderName)
                        .ToList();

                    // Log them
                    if (originalCourses.Count > 0)
                    {
                        logWriter.WriteLine($"Total (old) Courses with matching plan UIDs Found: {originalCourses.Count}");
                        Console.WriteLine($"Total (old) Courses with matching plan UIDs Found: {originalCourses.Count}");

                        foreach (var course in originalCourses)
                        {
                            logWriter.WriteLine($"  - Plan ID: {plan.Id}, Course: {course.Id}, UID: {plan.UID}");
                            Console.WriteLine($"  - Plan ID: {plan.Id}, Course: {course.Id}, UID: {plan.UID}");
                        }
                    }
                    else
                    {
                        logWriter.WriteLine("No (old) courses matching plan UID found.");
                        Console.WriteLine("No (old) courses matching plan UID found.");
                    }



                    // Check if plan already exists in vitesseBackupFolderName
                    var existingPlan = vitesseBackupCourse.PlanSetups.FirstOrDefault(p => p.Id == plan.Id);
                    if (existingPlan == null)
                    {
                        var copiedPlan = vitesseBackupCourse.CopyPlanSetup(plan);
                        if (copiedPlan != null)
                        {
                            logWriter.WriteLine($"Copied plan {plan.Id} to {vitesseBackupFolderName}");
                            Console.WriteLine($"Copied plan {plan.Id} to {vitesseBackupFolderName}");

                            // Remove the original plan from all courses that are not vitesseBackupFolderName
                            if (removeOriginalPlan)
                            {
                                foreach (var course in originalCourses)
                                {
                                    var planToRemove = course.PlanSetups.FirstOrDefault(p => p.UID == plan.UID);
                                    if (planToRemove != null)
                                    {
                                        course.RemovePlanSetup(planToRemove);
                                        logWriter.WriteLine($"Removed original plan {planToRemove.Id} from (old) course {course.Id}");
                                        Console.WriteLine($"Removed original plan {planToRemove.Id} from (old) course {course.Id}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            logWriter.WriteLine($"Failed to copy plan {plan.Id} to {vitesseBackupFolderName}.");
                            Console.WriteLine($"Failed to copy plan {plan.Id} to {vitesseBackupFolderName}.");
                        }
                    }
                    else
                    {
                        logWriter.WriteLine($"Plan {plan.Id} already exists in {vitesseBackupFolderName}, skipping copy.");
                        Console.WriteLine($"Plan {plan.Id} already exists in {vitesseBackupFolderName}, skipping copy.");
                    }
                }
            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"Error moving study to {vitesseBackupFolderName}: {ex.Message}");
                Console.WriteLine($"Error moving study to {vitesseBackupFolderName}: {ex.Message}");
            }
            finally
            {
                if (patient != null)
                {
                    app.SaveModifications();
                    app.ClosePatient();
                }
            }
        }


        static void RenameCourseWithCorrectPlansAndRemovePlansFromIncorrectCourses(Application app, string patientId, List<string> sopInstanceUIDs, StreamWriter logWriter, string vitesseBackupFolderName, bool removePlansFromIncorrectCourses)
        {
            ESAPI_Patient patient = null;


            try
            {
                patient = app.OpenPatientById(patientId);
                if (patient == null)
                {
                    logWriter.WriteLine("Patient not found in ARIA.");
                    Console.WriteLine("Patient not found in ARIA.");
                    return;
                }

                logWriter.WriteLine($"Processing patient {patientId} for potential course rename.");
                Console.WriteLine($"Processing patient {patientId} for potential course rename.");

                // Log all existing courses and their plans
                var allCourses = patient.Courses.ToList();
                logWriter.WriteLine($"Total Courses Found: {allCourses.Count}");
                Console.WriteLine($"Total Courses Found: {allCourses.Count}");

                foreach (var course in allCourses)
                {
                    int planCount = course.PlanSetups.Count();  // Fix: Using .Count() instead of .Count

                    logWriter.WriteLine($"Course ID: {course.Id}, Plan Count: {planCount}");
                    Console.WriteLine($"Course ID: {course.Id}, Plan Count: {planCount}");
                    foreach (var plan in course.PlanSetups)
                    {
                        logWriter.WriteLine($"  - Plan ID: {plan.Id}, UID: {plan.UID}");
                        Console.WriteLine($"  - Plan ID: {plan.Id}, UID: {plan.UID}");
                    }
                }

                // Find the course that contains exactly the provided plans
                var matchingCourses = allCourses
                    .Where(course => course.PlanSetups.Select(plan => plan.UID).OrderBy(uid => uid)
                                      .SequenceEqual(sopInstanceUIDs.OrderBy(uid => uid)))
                    .ToList();

                if (matchingCourses.Count == 0)
                {
                    logWriter.WriteLine("No course found that contains exactly and only the correct plans.");
                    Console.WriteLine("No course found that contains exactly and only the correct plans.");
                }

                foreach (var course in matchingCourses)
                {

                    if (course.Id != vitesseBackupFolderName) // Only rename if needed
                    {
                        logWriter.WriteLine($"Found matching course: {course.Id}. Renaming to '{vitesseBackupFolderName}'.");
                        Console.WriteLine($"Found matching course: {course.Id}. Renaming to '{vitesseBackupFolderName}'.");

                        patient.BeginModifications();
                        var oldname = course.Id;
                        course.Id = vitesseBackupFolderName;
                        app.SaveModifications();

                        logWriter.WriteLine($"Successfully renamed course {oldname} to '{vitesseBackupFolderName}'.");
                        Console.WriteLine($"Successfully renamed course {oldname} to '{vitesseBackupFolderName}'.");
                    }
                    else
                    {
                        logWriter.WriteLine($"Course {course.Id} already named '{vitesseBackupFolderName}'. Skipping rename.");
                        Console.WriteLine($"Course {course.Id} already named '{vitesseBackupFolderName}'. Skipping rename.");
                    }

                }

                if (removePlansFromIncorrectCourses)
                {
                    // Find matching courses again
                    allCourses = patient.Courses.ToList();
                    matchingCourses = allCourses
                        .Where(course => course.PlanSetups.Select(plan => plan.UID).OrderBy(uid => uid)
                                          .SequenceEqual(sopInstanceUIDs.OrderBy(uid => uid)))
                        .ToList();

                    // Remove the original plans from all Courses NOT in matchingCourses
                    foreach (var course in allCourses.Except(matchingCourses))
                    {

                        var plansToRemove = course.PlanSetups.Where(plan => sopInstanceUIDs.Contains(plan.UID)).ToList();

                        if (plansToRemove.Count > 0)
                        {

                            // **Check if all plans are "UnApproved"**
                            bool allPlansModifiable = plansToRemove.All(plan =>
                                plan.ApprovalStatus == PlanSetupApprovalStatus.UnApproved);

                            if (!allPlansModifiable)
                            {
                                logWriter.WriteLine($"Skipping plan removal from course {course.Id} due to one or more non-unapproved plans.");
                                Console.WriteLine($"Skipping plan removal from course {course.Id} due to one or more non-unapproved plans.");
                                continue; // **Skip this course entirely**
                            }


                            patient.BeginModifications();
                            foreach (var plan in plansToRemove)
                            {   
                                string planID = plan.Id;
                                course.RemovePlanSetup(plan);
                                logWriter.WriteLine($"Removed plan {planID} from course {course.Id}");
                                Console.WriteLine($"Removed plan {planID} from course {course.Id}");
                            }
                            app.SaveModifications();
                        }
                    }
                }



            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Error: {ex.Message}");
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
