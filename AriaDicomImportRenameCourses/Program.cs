using System;
using System.IO;
using System.Linq;
using VMS.TPS.Common.Model.API;
using System.Windows;

namespace VMS.TPS
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("Waiting for Enter:");
            Console.ReadLine();
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
                Console.WriteLine("Writing errors:");
                // Log the error details
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
            Console.ReadLine();

            // Directory where patient folders are stored
            var storagePath = @"H:\CCSI\PlanningModule\Physics Patient QA\VitesseBackup\Vitesse DICOM Archive1";

            // Get all patient folders, sorted alphabetically
            var patientFolders = Directory.GetDirectories(storagePath).OrderBy(f => Path.GetFileName(f)).ToList();

            // Get the first and last patient folder names for the log file name
            string firstPatientName = Path.GetFileName(patientFolders.First());
            string lastPatientName = Path.GetFileName(patientFolders.Last());

            // Define the log file name
            string logFileName = $"renaming_courses_global_log_summary_and_failures_{firstPatientName}_to_{lastPatientName}.txt";
            string logFilePath = Path.Combine(storagePath, logFileName);

            using (StreamWriter logWriter = new StreamWriter(logFilePath))
            {
                // Define the start and end indices (1-based indexing)
                int startIndex = 8; // Change this to the starting patient index
                int endIndex = 8; // Change this to the ending patient index

                // Process only the patients within the specified range
                for (int i = startIndex - 1; i < endIndex && i < patientFolders.Count; i++)
                {
                    var patientFolder = patientFolders[i];
                    string patientName = Path.GetFileName(patientFolder);

                    // Write the patient folder name to the log
                    logWriter.WriteLine($"Processing Patient Folder: {patientName}");
                    Console.WriteLine($"Processing Patient Folder: {patientName}");
                    
                    // Get fraction subfolders
                    var fractionFolders = Directory.GetDirectories(patientFolder);
                    foreach (var fractionFolder in fractionFolders)
                    {
                        var importFolderPath = Path.Combine(fractionFolder, "Edited for Eclipse Import");
                        if (Directory.Exists(importFolderPath))
                        {
                            // Find the plan file starting with "PL"
                            var planFiles = Directory.GetFiles(importFolderPath)
                                .Where(f => Path.GetFileName(f).StartsWith("PL")).ToList();

                            if (planFiles.Any())
                            {
                                foreach (var planFile in planFiles)
                                {
                                    // Read the DICOM file and extract the Patient ID (0010, 0020)
                                    var dcm = EvilDICOM.Core.DICOMObject.Read(planFile);
                                    var patientIDElement = dcm.FindFirst("00100020") as EvilDICOM.Core.Element.LongString;

                                    if (patientIDElement != null)
                                    {
                                        var patientId = patientIDElement.Data;
                                        logWriter.WriteLine($"Found Patient ID: {patientId}");
                                        Console.WriteLine($"Found Patient ID: {patientId}");

                                        Console.ReadLine();
                                        // Extract Study Instance UID (0020, 000D)
                                        var studyInstanceUIDElement = dcm.FindFirst("0020000D") as EvilDICOM.Core.Element.UniqueIdentifier;
                                        if (studyInstanceUIDElement != null)
                                        {
                                            var studyInstanceUID = studyInstanceUIDElement.Data;
                                            logWriter.WriteLine($"Found Study Instance UID: {studyInstanceUID}");
                                            Console.WriteLine($"Found Study Instance UID: {studyInstanceUID}");

                                            // Call method to rename course based on Patient ID and Study Instance UID
                                            RenameCourseForPlan(app, patientId, studyInstanceUID, logWriter);
                                        }
                                        else
                                        {
                                            logWriter.WriteLine("Study Instance UID not found.");
                                            Console.WriteLine("Study Instance UID not found.");
                                        }
                                    }
                                    else
                                    {
                                        logWriter.WriteLine("Patient ID not found.");
                                        Console.WriteLine("Patient ID not found.");
                                    }
                                }
                            }
                            else
                            {
                                logWriter.WriteLine("No plan files found in the fraction folder.");
                                Console.WriteLine("No plan files found in the fraction folder.");
                            }
                        }
                    }

                    // Separator between patients
                    logWriter.WriteLine("___");
                    Console.WriteLine("___");
                }

                logWriter.WriteLine("Course renaming process completed.");
                Console.WriteLine("Course renaming process completed.");
                Console.Read();
            }
        }

        // Method to rename the course in ARIA using ESAPI
        static void RenameCourseForPlan(Application app, string patientId, string studyInstanceUID, StreamWriter logWriter)
        {
            Patient patient = null;

            try
            {
                // Use ESAPI to open the patient by Patient ID
                patient = app.OpenPatientById(patientId);

                if (patient != null)
                {
                    logWriter.WriteLine($"Opened Patient: {patientId}");
                    Console.WriteLine($"Opened Patient: {patientId}");

                    // Retrieve the list of courses for the patient
                    var courses = patient.Courses;

                    // Check for the specific course matching the Study Instance UID
                    foreach (var course in courses)
                    {
                        var matchingPlans = course.PlanSetups
                            .Where(plan => plan.UID == studyInstanceUID)
                            .ToList();

                        if (matchingPlans.Any())
                        {
                            logWriter.WriteLine($"Found matching plan in course {course.Id} for Study Instance UID: {studyInstanceUID}");
                            Console.WriteLine($"Found matching plan in course {course.Id} for Study Instance UID: {studyInstanceUID}");

                            // Generate a list of existing course names
                            var existingCourseNames = courses.Select(c => c.Id).ToList();

                            // Start with "Course Vitesse" and increment if necessary
                            string newCourseName = "Course Vitesse";
                            int courseNumber = 1;
                            while (existingCourseNames.Contains(newCourseName))
                            {
                                newCourseName = $"Course Vitesse {courseNumber}";
                                courseNumber++;
                            }

                            // Rename the course
                            try
                            {
                                logWriter.WriteLine($"Old Course Name: {course.Id}, New Course Name: {newCourseName}");
                                Console.WriteLine($"Old Course Name: {course.Id}, New Course Name: {newCourseName}");

                                course.Id = newCourseName;
                                logWriter.WriteLine($"Successfully renamed course to: {newCourseName}");
                                Console.WriteLine($"Successfully renamed course to: {newCourseName}");
                            }
                            catch (Exception ex)
                            {
                                logWriter.WriteLine($"Failed to rename course to {newCourseName}: {ex.Message}");
                                Console.WriteLine($"Failed to rename course to {newCourseName}: {ex.Message}");
                            }

                            break; // Exit after renaming the course
                        }
                        else
                        {
                            logWriter.WriteLine($"No matching plan found in course {course.Id} for Study Instance UID: {studyInstanceUID}");
                            Console.WriteLine($"No matching plan found in course {course.Id} for Study Instance UID: {studyInstanceUID}");
                        }
                    }
                }
                else
                {
                    logWriter.WriteLine("Patient not found.");
                    Console.WriteLine("Patient not found.");
                }
            }
            catch (Exception ex)
            {
                logWriter.WriteLine($"Failed to open patient or rename course: {ex.Message}");
                Console.WriteLine($"Failed to open patient or rename course: {ex.Message}");
            }
            finally
            {
                if (patient != null)
                {
                    app.ClosePatient();  // Always close the patient record when done
                }
            }
        }
    }
}
