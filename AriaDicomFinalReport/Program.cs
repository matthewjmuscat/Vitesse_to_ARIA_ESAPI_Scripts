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

namespace VMS.TPS
{
    class Program
    {
        // The configuration file should be placed alongside the executable.
        private static string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");

        /* Example config.xml:
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
            string expectedCourseName;  // This is the course name provided in the XML
            List<int> selectedIndices = ReadConfigFromXML(out storagePath, out expectedCourseName);

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

            var patientFolders = Directory.GetDirectories(storagePath)
                                          .OrderBy(f => Path.GetFileName(f))
                                          .ToList();

            if (patientFolders.Count == 0)
            {
                Console.WriteLine("No patient folders found in the specified storage path.");
                return;
            }

            // Ensure selected indices do not exceed available patient folders.
            selectedIndices = selectedIndices.Where(i => i < patientFolders.Count).ToList();

            if (selectedIndices.Count == 0)
            {
                Console.WriteLine("No valid patient indices after filtering out out-of-bounds indices.");
                return;
            }

            // Determine the first and last patient folder names for the log file.
            string firstPatientName = Path.GetFileName(patientFolders[selectedIndices.First()]);
            string lastPatientName = Path.GetFileName(patientFolders[selectedIndices.Last()]);

            // Create main log file with a timestamp and index details.
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logFilePath = Path.Combine(storagePath, $"course_plan_verification_log_{timestamp}_indices_{firstPatientName}_to_{lastPatientName}.txt");

            // Create failure log file. This log will only record patients with at least one failure.
            string failureLogFilePath = Path.Combine(storagePath, $"course_plan_verification_failure_log_{timestamp}.txt");

            using (StreamWriter logWriter = new StreamWriter(logFilePath))
            using (StreamWriter failureLogWriter = new StreamWriter(failureLogFilePath))
            {
                logWriter.WriteLine($"Verifying patient courses and plans for patient folders from {firstPatientName} to {lastPatientName}");
                Console.WriteLine($"Verifying patient courses and plans for patient folders from {firstPatientName} to {lastPatientName}");
                failureLogWriter.WriteLine("Failure Log - Patients with one or more errors");
                failureLogWriter.WriteLine("=============================================");

                // Process each patient folder specified by the indices.
                foreach (var i in selectedIndices)
                {
                    string patientFolder = patientFolders[i];
                    string patientFolderName = Path.GetFileName(patientFolder);
                    logWriter.WriteLine($"Processing Patient Folder: {patientFolderName}");
                    Console.WriteLine($"Processing Patient Folder: {patientFolderName}");

                    // For each patient folder, collect the plan UIDs from its fraction folders.
                    List<string> fractionPlanUIDs = new List<string>();
                    string patientId = string.Empty;

                    var fractionFolders = Directory.GetDirectories(patientFolder);
                    foreach (var fractionFolder in fractionFolders)
                    {
                        string fractionFolderName = Path.GetFileName(fractionFolder);
                        logWriter.WriteLine($"Processing Fraction Folder: {fractionFolderName}");
                        Console.WriteLine($"Processing Fraction Folder: {fractionFolderName}");

                        string importFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import");
                        if (Directory.Exists(importFolderPath))
                        {
                            // Assume the plan file is always named "PL001.dcm"
                            string planFilePath = Path.Combine(importFolderPath, "PL001.dcm");
                            if (File.Exists(planFilePath))
                            {
                                logWriter.WriteLine($"Reading plan file: {planFilePath}");
                                Console.WriteLine($"Reading plan file: {planFilePath}");
                                try
                                {
                                    var dcm = DICOMObject.Read(planFilePath);
                                    var patientIDElement = dcm.FindFirst("00100020") as EvilDICOM.Core.Element.LongString;
                                    var sopInstanceUIDElement = dcm.FindFirst("00080018") as EvilDICOM.Core.Element.UniqueIdentifier;

                                    if (patientIDElement != null && sopInstanceUIDElement != null)
                                    {
                                        patientId = patientIDElement.Data;
                                        string sopInstanceUID = sopInstanceUIDElement.Data;
                                        fractionPlanUIDs.Add(sopInstanceUID);
                                        logWriter.WriteLine($"Found Patient ID: {patientId}, SOP Instance UID: {sopInstanceUID}");
                                        Console.WriteLine($"Found Patient ID: {patientId}, SOP Instance UID: {sopInstanceUID}");
                                    }
                                    else
                                    {
                                        logWriter.WriteLine("Patient ID or SOP Instance UID not found in plan file.");
                                        Console.WriteLine("Patient ID or SOP Instance UID not found in plan file.");
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
                        else
                        {
                            logWriter.WriteLine("Edited for Eclipse Import folder not found in fraction folder.");
                            Console.WriteLine("Edited for Eclipse Import folder not found in fraction folder.");
                        }
                    }

                    // If no valid patient ID was retrieved, log a message and record the failure.
                    if (string.IsNullOrEmpty(patientId))
                    {
                        logWriter.WriteLine($"No valid Patient ID found in fraction folders for patient folder {patientFolderName}.");
                        Console.WriteLine($"No valid Patient ID found in fraction folders for patient folder {patientFolderName}.");
                        failureLogWriter.WriteLine($"Patient Folder: {patientFolderName}, Patient ID: Not Found");
                        continue;
                    }

                    // Remove duplicate plan UIDs, if any.
                    fractionPlanUIDs = fractionPlanUIDs.Distinct().ToList();

                    // Now perform the verification and retrieve a flag indicating whether a failure was detected.
                    bool failed = VerifyPatientCourseAndPlans(app, patientId, fractionPlanUIDs, expectedCourseName, logWriter);
                    if (failed)
                    {
                        // Log basic patient info in the failure log.
                        failureLogWriter.WriteLine($"Patient Folder: {patientFolderName}, Patient ID: {patientId}");
                    }
                    logWriter.WriteLine("-----------------------------------------------------");
                }
            }
        }

        /// <summary>
        /// Verifies for a given patient that:
        /// 1. A course exists whose name matches the expected course name (from XML).
        /// 2. The plan UIDs in that course match exactly the ones retrieved from the fraction folders.
        /// 3. Whether each of those plan UIDs appears in any courses other than the expected course.
        /// The results of each check are logged.
        /// Returns true if any check fails.
        /// </summary>
        static bool VerifyPatientCourseAndPlans(Application app, string patientId, List<string> fractionPlanUIDs, string expectedCourseName, StreamWriter logWriter)
        {
            bool failed = false;
            logWriter.WriteLine($"Verifying patient with ID: {patientId}");
            Console.WriteLine($"Verifying patient with ID: {patientId}");
            ESAPI_Patient patient = null;

            try
            {
                patient = app.OpenPatientById(patientId);
                if (patient == null)
                {
                    logWriter.WriteLine("Patient not found in ARIA.");
                    Console.WriteLine("Patient not found in ARIA.");
                    failed = true;
                    return failed;
                }

                var allCourses = patient.Courses.ToList();
                logWriter.WriteLine($"Total courses found for patient {patientId}: {allCourses.Count}");
                Console.WriteLine($"Total courses found for patient {patientId}: {allCourses.Count}");

                // 1. Check for existence of the expected course.
                var targetCourse = allCourses.FirstOrDefault(c => c.Id == expectedCourseName);
                if (targetCourse == null)
                {
                    logWriter.WriteLine($"No course named '{expectedCourseName}' exists for patient {patientId}.");
                    Console.WriteLine($"No course named '{expectedCourseName}' exists for patient {patientId}.");
                    failed = true;
                }
                else
                {
                    logWriter.WriteLine($"Course '{expectedCourseName}' exists for patient {patientId}.");
                    Console.WriteLine($"Course '{expectedCourseName}' exists for patient {patientId}.");

                    // 2. Compare the plan UIDs in the expected course with those retrieved from the fraction folders.
                    var targetCoursePlanUIDs = targetCourse.PlanSetups.Select(p => p.UID)
                                                                      .Distinct()
                                                                      .ToList();
                    logWriter.WriteLine($"Plan UIDs in course '{expectedCourseName}': {string.Join(", ", targetCoursePlanUIDs)}");
                    Console.WriteLine($"Plan UIDs in course '{expectedCourseName}': {string.Join(", ", targetCoursePlanUIDs)}");

                    var fractionSet = new HashSet<string>(fractionPlanUIDs);
                    var courseSet = new HashSet<string>(targetCoursePlanUIDs);

                    if (fractionSet.SetEquals(courseSet))
                    {
                        logWriter.WriteLine("Yes, the plan UIDs in the course match exactly the ones retrieved from the fraction folders.");
                        Console.WriteLine("Yes, the plan UIDs in the course match exactly the ones retrieved from the fraction folders.");
                    }
                    else
                    {
                        logWriter.WriteLine("No, there is a mismatch between the plan UIDs in the course and those from the fraction folders.");
                        Console.WriteLine("No, there is a mismatch between the plan UIDs in the course and those from the fraction folders.");
                        failed = true;

                        var inCourseNotFraction = courseSet.Except(fractionSet);
                        var inFractionNotCourse = fractionSet.Except(courseSet);

                        if (inCourseNotFraction.Any())
                        {
                            logWriter.WriteLine($"Plan UIDs present in course but not in fraction folders: {string.Join(", ", inCourseNotFraction)}");
                            Console.WriteLine($"Plan UIDs present in course but not in fraction folders: {string.Join(", ", inCourseNotFraction)}");
                        }
                        if (inFractionNotCourse.Any())
                        {
                            logWriter.WriteLine($"Plan UIDs present in fraction folders but not in course: {string.Join(", ", inFractionNotCourse)}");
                            Console.WriteLine($"Plan UIDs present in fraction folders but not in course: {string.Join(", ", inFractionNotCourse)}");
                        }
                    }
                }

                // 3. For each plan UID from the fraction folders, check if it appears in any other courses.
                foreach (var planUID in fractionPlanUIDs)
                {
                    // Get all courses that contain this plan UID.
                    var coursesWithPlan = allCourses
                        .Where(c => c.PlanSetups.Any(p => p.UID == planUID))
                        .Select(c => c.Id)
                        .Distinct()
                        .ToList();

                    // If the expected course exists, remove it from the list.
                    if (targetCourse != null)
                    {
                        coursesWithPlan.Remove(expectedCourseName);
                    }

                    if (coursesWithPlan.Count > 0)
                    {
                        logWriter.WriteLine($"Plan UID '{planUID}' is also found in other course(s): {string.Join(", ", coursesWithPlan)}");
                        Console.WriteLine($"Plan UID '{planUID}' is also found in other course(s): {string.Join(", ", coursesWithPlan)}");
                        failed = true;
                    }
                    else
                    {
                        logWriter.WriteLine($"Plan UID '{planUID}' is not found in any other courses.");
                        Console.WriteLine($"Plan UID '{planUID}' is not found in any other courses.");
                    }
                }
            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"Error verifying patient {patientId}: {ex.Message}");
                Console.WriteLine($"Error verifying patient {patientId}: {ex.Message}");
                failed = true;
            }
            finally
            {
                if (patient != null)
                {
                    app.ClosePatient();
                }
            }
            return failed;
        }

        /// <summary>
        /// Reads configuration settings from the XML file and returns a list of patient indices.
        /// </summary>
        static List<int> ReadConfigFromXML(out string storagePath, out string expectedCourseName)
        {
            List<int> indices = new List<int>();
            storagePath = "";
            expectedCourseName = "Vitesse Backup"; // Default name if not specified

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

                // Read the expected course name.
                expectedCourseName = xmlDoc.SelectSingleNode("//VitesseBackupFolderName")?.InnerText.Trim() ?? "Vitesse Backup";

                var patientFolders = Directory.GetDirectories(storagePath)
                                              .OrderBy(f => Path.GetFileName(f))
                                              .ToList();
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
