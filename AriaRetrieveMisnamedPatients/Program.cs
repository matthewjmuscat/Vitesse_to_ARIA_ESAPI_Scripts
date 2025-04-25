using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using System.Globalization;

// Create an alias to avoid ambiguity
using EsapiApplication = VMS.TPS.Common.Model.API.Application;

[assembly: ESAPIScript(IsWriteable = false)]  // Read-only script (no modifications needed)

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
                using (EsapiApplication app = EsapiApplication.CreateApplication())  // Use alias here
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

        static void Execute(EsapiApplication app)  // Use alias here
        {
            // Define CSV file path
            string storagePath = @"\\srvnetapp02.phsabc.ehcnet.ca\bcca\docs\CCSI\PlanningModule\Physics Patient QA\VitesseBackup";
            string csvFilePath = Path.Combine(storagePath, "patients_misnamed_BCCIDs.csv");

            if (!File.Exists(csvFilePath))
            {
                Console.WriteLine("CSV file not found. Exiting.");
                return;
            }

            // Read CSV file and store BCCIDs (Patient IDs)
            List<string> patientIds = new List<string>();
            using (StreamReader reader = new StreamReader(csvFilePath))
            {
                // Skip header line
                string headerLine = reader.ReadLine();

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string patientId = line.Trim().Replace("\"", ""); // Remove quotes around patient id number
                    if (!string.IsNullOrEmpty(patientId))
                    {
                        patientIds.Add(patientId);
                    }
                }
            }

            Console.WriteLine($"Found {patientIds.Count} BCCIDs to search in ARIA.");

            // Create log file
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logFilePath = Path.Combine(storagePath, $"aria_patient_names_{timestamp}.txt");
            string csvOutputFilePath = Path.Combine(storagePath, $"aria_patient_names_{timestamp}_results.csv");


            using (StreamWriter logWriter = new StreamWriter(logFilePath))
            using (StreamWriter csvWriter = new StreamWriter(csvOutputFilePath))

            {
                logWriter.WriteLine($"Retrieved patient names for {patientIds.Count} BCCIDs.");
                Console.WriteLine($"Retrieving patient names for {patientIds.Count} BCCIDs...");

                // Write header row for CSV output
                csvWriter.WriteLine("BCCID,LastName,FirstName");

                foreach (string patientId in patientIds)
                {
                    Console.WriteLine($"Processing BCCID: {patientId}");

                    try
                    {
                        var patient = app.OpenPatientById(patientId);
                        if (patient != null)
                        {
                            string firstName = patient.FirstName;
                            string lastName = patient.LastName;

                            logWriter.WriteLine($"{patientId}, {lastName}, {firstName}");
                            csvWriter.WriteLine($"{patientId},{lastName},{firstName}");
                            Console.WriteLine($"Found: {lastName}, {firstName}");

                            app.ClosePatient();
                        }
                        else
                        {
                            logWriter.WriteLine($"{patientId}, NOT FOUND");
                            csvWriter.WriteLine($"{patientId},,");
                            Console.WriteLine($"Patient {patientId} not found in ARIA.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logWriter.WriteLine($"{patientId}, ERROR: {ex.Message}");
                        csvWriter.WriteLine($"{patientId},ERROR,ERROR");
                        Console.WriteLine($"Error retrieving {patientId}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"Finished processing. Results saved to: {logFilePath}");
            Console.WriteLine($"CSV output saved to: {csvOutputFilePath}");

        }
    }
}
