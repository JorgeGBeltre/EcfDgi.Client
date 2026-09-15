using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EcfDgii.TestRunner
{
    public class SimulationRunner
    {
        private readonly EcfXmlSigner _signer;
        private readonly DgiiDirectTransport _transport;
        private readonly string _rncEmisor = "133664692";
        private readonly string _unsignedXmlsDir = @"C:\Users\Jorge\Desktop\Ecf\scratch\generated_xmls";
        private readonly string _xsdDir = @"C:\Users\Jorge\Desktop\Ecf\EcfDgi.Client\Documentación Técnica (XSD)";
        private readonly string _outputBaseDir = @"C:\Users\Jorge\Desktop\Ceramic chic\Simulacion_Operaciones_Reales";

        public SimulationRunner(EcfXmlSigner signer, DgiiDirectTransport transport)
        {
            _signer = signer;
            _transport = transport;
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public class SimulationCase
        {
            public string OriginalFile { get; set; } = "";
            public string NewEncf { get; set; } = "";
            public string TipoEcf { get; set; } = "";
            public string Description { get; set; } = "";
            public string? ReferenciaEncf { get; set; }
            public bool IsRfce { get; set; }
            public bool IsBaseInvoice { get; set; }
            public bool IsNote { get; set; }
            public string SignedXml { get; set; } = "";
            public string SecurityCode { get; set; } = "";
            public string TrackId { get; set; } = "";
            public string EstadoDgii { get; set; } = "Pendiente";
            public string MensajesDgii { get; set; } = "";
            public string RncComprador { get; set; } = "";
            public string? IdExtranjero { get; set; }
            public string RazonSocialComprador { get; set; } = "";
            public decimal MontoTotal { get; set; }
            public decimal TotalItbis { get; set; }
            public string FechaEmision { get; set; } = "";
            public string FechaFirma { get; set; } = "";
            public List<(string Nombre, decimal Cantidad, decimal Precio, decimal Monto)> Items { get; set; } = new();
        }

        public async Task RunAsync()
        {
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("INICIANDO FASE 2: SIMULACION CON DATOS DE OPERACIONES REALES (CERAMIC CHIC SRL - 133664692)");
            Console.WriteLine("=========================================================================================================");

            var xmlsDir = Path.Combine(_outputBaseDir, "XMLs");
            var rfceDir = Path.Combine(_outputBaseDir, "RFCE");
            var b2cDir = Path.Combine(_outputBaseDir, "Consumo_Menor_250k");
            var pdfsDir = Path.Combine(_outputBaseDir, "PDFs");

            if (Directory.Exists(xmlsDir)) Directory.Delete(xmlsDir, true);
            if (Directory.Exists(rfceDir)) Directory.Delete(rfceDir, true);
            if (Directory.Exists(b2cDir)) Directory.Delete(b2cDir, true);
            if (Directory.Exists(pdfsDir)) Directory.Delete(pdfsDir, true);

            Directory.CreateDirectory(xmlsDir);
            Directory.CreateDirectory(rfceDir);
            Directory.CreateDirectory(b2cDir);
            Directory.CreateDirectory(pdfsDir);

            // 1. Define the 25 Simulation Cases
            var cases = DefineCases();

            // 2. Generate and Sign each e-CF XML
            Console.WriteLine("\n[1/6] Generando y firmando 25 comprobantes con catálogo comercial de CERAMIC CHIC SRL...");
            var validator = new EcfSchemaValidator();
            int secondOffset = 0;

            foreach (var c in cases)
            {
                secondOffset += 5;
                var sourcePath = Path.Combine(_unsignedXmlsDir, $"{c.OriginalFile}.xml");
                if (!File.Exists(sourcePath))
                {
                    Console.WriteLine($"[ERROR] Plantilla fuente no encontrada: {sourcePath}");
                    continue;
                }

                var rawXml = File.ReadAllText(sourcePath, Encoding.UTF8);

                // Update Sequence
                rawXml = Regex.Replace(rawXml, @"<eNCF>.*?</eNCF>", $"<eNCF>{c.NewEncf}</eNCF>");

                // Update Emisor
                rawXml = Regex.Replace(rawXml, @"<RazonSocialEmisor>.*?</RazonSocialEmisor>", "<RazonSocialEmisor>CERAMIC CHIC SRL</RazonSocialEmisor>");
                rawXml = Regex.Replace(rawXml, @"<NombreComercial>.*?</NombreComercial>", "<NombreComercial>CERAMIC CHIC</NombreComercial>");

                // Update Timestamps
                c.FechaEmision = DateTime.Now.ToString("dd-MM-yyyy");
                c.FechaFirma = DateTime.Now.AddSeconds(secondOffset).ToString("dd-MM-yyyy HH:mm:ss");
                rawXml = Regex.Replace(rawXml, @"<FechaEmision>.*?</FechaEmision>", $"<FechaEmision>{c.FechaEmision}</FechaEmision>");
                rawXml = Regex.Replace(rawXml, @"<FechaHoraFirma>.*?</FechaHoraFirma>", $"<FechaHoraFirma>{c.FechaFirma}</FechaHoraFirma>");

                // Update References if Credit/Debit Note
                if (!string.IsNullOrEmpty(c.ReferenciaEncf))
                {
                    rawXml = Regex.Replace(rawXml, @"<NCFModificado>.*?</NCFModificado>", $"<NCFModificado>{c.ReferenciaEncf}</NCFModificado>");
                    rawXml = Regex.Replace(rawXml, @"<FechaNCFModificado>.*?</FechaNCFModificado>", $"<FechaNCFModificado>{c.FechaEmision}</FechaNCFModificado>");
                    rawXml = Regex.Replace(rawXml, @"<IndicadorNotaCredito>.*?</IndicadorNotaCredito>", "<IndicadorNotaCredito>0</IndicadorNotaCredito>");
                }

                // Sign XML
                c.SignedXml = _signer.SignXml(rawXml, _rncEmisor);
                c.SecurityCode = _signer.ExtractSignatureValue(c.SignedXml).Substring(0, 6);

                // Parse Totals and Items for PDF generation
                ParseTotalsAndItems(c);

                // Validate Schema
                var xsdFile = EcfXsdFileNameResolver.Resolve(c.SignedXml);
                if (!string.IsNullOrEmpty(xsdFile))
                {
                    var xsdPath = Path.Combine(_xsdDir, xsdFile);
                    if (File.Exists(xsdPath))
                    {
                        var valResult = validator.Validate(c.SignedXml, xsdPath);
                        if (!valResult.IsValid)
                        {
                            Console.WriteLine($"[XSD WARNING] {c.NewEncf}: {string.Join("; ", valResult.Errors)}");
                        }
                    }
                }

                // Save signed XML
                var fileName = $"{_rncEmisor}{c.NewEncf}.xml";
                File.WriteAllText(Path.Combine(xmlsDir, fileName), c.SignedXml, Encoding.UTF8);

                if (c.IsRfce)
                {
                    var b2cXmls = Path.Combine(b2cDir, "XMLs");
                    Directory.CreateDirectory(b2cXmls);
                    File.WriteAllText(Path.Combine(b2cDir, fileName), c.SignedXml, Encoding.UTF8);
                    File.WriteAllText(Path.Combine(b2cXmls, fileName), c.SignedXml, Encoding.UTF8);
                }

                Console.WriteLine($"  [OK] {c.NewEncf} ({c.Description}) -> Monto: RD$ {c.MontoTotal:N2} | CodSeg: {c.SecurityCode}");
            }

            // Create ZIP of the 4 E32 < 250k for OFV Upload
            var b2cXmlsDir = Path.Combine(b2cDir, "XMLs");
            var zipPath = Path.Combine(b2cDir, $"{_rncEmisor}_Facturas_Consumo_Menor_250k.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (Directory.Exists(b2cXmlsDir))
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(b2cXmlsDir, zipPath);
                Console.WriteLine($"\n[INFO] Archivo ZIP para subir en OFV creado en: {zipPath}");
            }

            // 3. PHASE 1: Send 4 RFCEs via RecepcionFC
            Console.WriteLine("\n[2/6] Transmitiendo los 4 Resúmenes B2C (RFCE) vía RecepcionFC...");
            var rfceCases = cases.FindAll(c => c.IsRfce);
            int rfceAccepted = 0;
            foreach (var c in rfceCases)
            {
                var rfceXml = BuildAndSignRfce(c);
                var rfceFileName = $"{_rncEmisor}{c.NewEncf}.xml";
                File.WriteAllText(Path.Combine(rfceDir, $"RFCE_{c.NewEncf}.xml"), rfceXml, Encoding.UTF8);

                Console.Write($"  Enviando RFCE {c.NewEncf} (Cod: {c.SecurityCode})... ");
                try
                {
                    var resp = await _transport.SendRfceAsync(rfceXml, rfceFileName);
                    Console.WriteLine($"-> Estado: {resp.Estado} (Codigo: {resp.Codigo})");
                    if (resp.Codigo == 1 || resp.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        rfceAccepted++;
                        c.EstadoDgii = "Aceptado";
                    }
                    else
                    {
                        c.EstadoDgii = resp.Estado ?? "Rechazado";
                        if (resp.Mensajes != null)
                            c.MensajesDgii = string.Join("; ", resp.Mensajes.ConvertAll(m => m.Valor));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"-> ERROR: {ex.Message}");
                    c.EstadoDgii = "Error";
                    c.MensajesDgii = ex.Message;
                }
                await Task.Delay(400);
            }

            if (rfceAccepted != rfceCases.Count)
            {
                Console.WriteLine($"[ERROR] Solo {rfceAccepted} de {rfceCases.Count} RFCEs fueron aceptados. Deteniendo para evitar reinicio.");
                return;
            }

            // 4. PHASE 2: Transmit 18 Base Invoices via Recepcion
            Console.WriteLine("\n[3/6] Transmitiendo las 18 Facturas Base vía Recepcion...");
            var baseCases = cases.FindAll(c => c.IsBaseInvoice);
            foreach (var c in baseCases)
            {
                var fileName = $"{_rncEmisor}{c.NewEncf}.xml";
                Console.Write($"  Enviando {c.NewEncf}... ");
                try
                {
                    var resp = await _transport.SendEcfAsync(c.SignedXml, fileName);
                    c.TrackId = resp.TrackId;
                    Console.WriteLine($"-> TrackId: {c.TrackId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"-> ERROR: {ex.Message}");
                    c.EstadoDgii = "Error";
                    c.MensajesDgii = ex.Message;
                }
                await Task.Delay(300);
            }

            // 5. PHASE 3: Poll until all 18 Base Invoices are Aceptado
            Console.WriteLine("\n[4/6] Verificando validación de las 18 Facturas Base ante DGII...");
            bool allBaseAccepted = false;
            for (int attempt = 1; attempt <= 12; attempt++)
            {
                Console.Write($"  Intento {attempt}/12: Consultando estado... ");
                await Task.Delay(5000);
                int countAccepted = 0;
                foreach (var c in baseCases)
                {
                    if (!string.IsNullOrEmpty(c.TrackId))
                    {
                        try
                        {
                            var res = await _transport.ConsultarResultadoAsync(c.TrackId);
                            if (res.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                countAccepted++;
                                c.EstadoDgii = "Aceptado";
                            }
                        }
                        catch { }
                    }
                }
                Console.WriteLine($"{countAccepted} de {baseCases.Count} aceptadas.");
                if (countAccepted == baseCases.Count)
                {
                    allBaseAccepted = true;
                    break;
                }
            }

            if (!allBaseAccepted)
            {
                Console.WriteLine("[WARN] No todas las facturas base han sido confirmadas. Esperando 10s extra...");
                await Task.Delay(10000);
            }
            else
            {
                Console.WriteLine("[SUCCESS] ¡Las 18 Facturas Base están ACEPTADAS! Esperando 8s para indexación de referencias...");
                await Task.Delay(8000);
            }

            // 6. PHASE 4: Transmit 3 Credit/Debit Notes
            Console.WriteLine("\n[5/6] Transmitiendo las 3 Notas de Crédito / Débito...");
            var noteCases = cases.FindAll(c => c.IsNote);
            foreach (var c in noteCases)
            {
                var fileName = $"{_rncEmisor}{c.NewEncf}.xml";
                Console.Write($"  Enviando Nota {c.NewEncf} (modifica {c.ReferenciaEncf})... ");
                try
                {
                    var resp = await _transport.SendEcfAsync(c.SignedXml, fileName);
                    c.TrackId = resp.TrackId;
                    Console.WriteLine($"-> TrackId: {c.TrackId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"-> ERROR: {ex.Message}");
                    c.EstadoDgii = "Error";
                    c.MensajesDgii = ex.Message;
                }
                await Task.Delay(2000);
            }

            Console.WriteLine("\nEsperando procesamiento de notas en DGII...");
            for (int noteAttempt = 1; noteAttempt <= 8; noteAttempt++)
            {
                await Task.Delay(5000);
                int pendingNotes = 0;
                foreach (var c in noteCases)
                {
                    if (c.EstadoDgii != "Aceptado" && !string.IsNullOrEmpty(c.TrackId))
                    {
                        try
                        {
                            var res = await _transport.ConsultarResultadoAsync(c.TrackId);
                            if (res.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                c.EstadoDgii = "Aceptado";
                            }
                            else
                            {
                                pendingNotes++;
                                c.EstadoDgii = res.Estado ?? "En Proceso";
                                if (res.Mensajes != null && res.Mensajes.Count > 0)
                                {
                                    c.MensajesDgii = string.Join("; ", res.Mensajes.ConvertAll(m => m.Valor));
                                }
                            }
                        }
                        catch { pendingNotes++; }
                    }
                }
                Console.WriteLine($"  Intento {noteAttempt}/8: Notas pendientes: {pendingNotes}");
                if (pendingNotes == 0) break;
            }

            // Final check for any remaining comprobantes
            foreach (var c in cases)
            {
                if (!string.IsNullOrEmpty(c.TrackId) && c.EstadoDgii != "Aceptado")
                {
                    try
                    {
                        var res = await _transport.ConsultarResultadoAsync(c.TrackId);
                        c.EstadoDgii = res.Estado ?? "Desconocido";
                        if (res.Mensajes != null && res.Mensajes.Count > 0)
                        {
                            c.MensajesDgii = string.Join("; ", res.Mensajes.ConvertAll(m => m.Valor));
                        }
                    }
                    catch { }
                }
            }

            // 7. PHASE 5: Generate 25 Official Representation Impresa PDFs
            Console.WriteLine("\n[6/6] Generando 25 Representaciones Impresas oficiales en PDF con Código QR DGII...");
            foreach (var c in cases)
            {
                try
                {
                    var pdfPath = Path.Combine(pdfsDir, $"{c.NewEncf}.pdf");
                    GenerateInvoicePdf(c, pdfPath);
                    Console.WriteLine($"  [PDF OK] {c.NewEncf}.pdf");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [PDF ERROR] {c.NewEncf}: {ex.Message}");
                }
            }

            // Results Table
            Console.WriteLine("\n=========================================================================================================");
            Console.WriteLine("RESULTADOS DE SIMULACION CON DATOS REALES - CERAMIC CHIC SRL (133664692)");
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("{0,-15} | {1,-6} | {2,-38} | {3,-12} | {4}", "eNCF", "Tipo", "TrackId / Ref", "Estado DGII", "Mensajes");
            Console.WriteLine("---------------------------------------------------------------------------------------------------------");

            int totalAccepted = 0;
            foreach (var c in cases)
            {
                if (c.EstadoDgii.Equals("Aceptado", StringComparison.OrdinalIgnoreCase))
                {
                    totalAccepted++;
                }
                Console.WriteLine("{0,-15} | {1,-6} | {2,-38} | {3,-12} | {4}", c.NewEncf, c.TipoEcf, c.TrackId, c.EstadoDgii, c.MensajesDgii);
            }

            Console.WriteLine("=========================================================================================================");
            Console.WriteLine($"TOTAL ACEPTADOS: {totalAccepted} de {cases.Count} comprobantes.");
            Console.WriteLine($"Carpeta de Archivos XML: {xmlsDir}");
            Console.WriteLine($"Carpeta de Facturas < 250k: {b2cDir}");
            Console.WriteLine($"Carpeta de PDFs Oficiales: {pdfsDir}");
            Console.WriteLine("=========================================================================================================");
        }

        public async Task RunMissingAsync()
        {
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("COMPLETANDO COMPROBANTES FALTANTES DE SIMULACION: E310000000105 y E340000000103");
            Console.WriteLine("=========================================================================================================");

            var xmlsDir = Path.Combine(_outputBaseDir, "XMLs");
            var pdfsDir = Path.Combine(_outputBaseDir, "PDFs");

            var missingCases = new List<SimulationCase>
            {
                new SimulationCase 
                { 
                    OriginalFile = "E310000000005", 
                    NewEncf = "E310000000105", 
                    TipoEcf = "31", 
                    Description = "Crédito Fiscal - Porcelanato Italiano Pulido", 
                    IsBaseInvoice = true 
                },
                new SimulationCase 
                { 
                    OriginalFile = "E340000000015", 
                    NewEncf = "E340000000103", 
                    TipoEcf = "34", 
                    Description = "Nota Crédito - Descuento Comercial Concedido", 
                    ReferenciaEncf = "E410000000102", 
                    IsNote = true 
                }
            };

            int secondOffset = 50;
            foreach (var c in missingCases)
            {
                secondOffset += 10;
                var sourcePath = Path.Combine(_unsignedXmlsDir, $"{c.OriginalFile}.xml");
                var rawXml = File.ReadAllText(sourcePath, Encoding.UTF8);

                rawXml = Regex.Replace(rawXml, @"<eNCF>.*?</eNCF>", $"<eNCF>{c.NewEncf}</eNCF>");
                rawXml = Regex.Replace(rawXml, @"<RazonSocialEmisor>.*?</RazonSocialEmisor>", "<RazonSocialEmisor>CERAMIC CHIC SRL</RazonSocialEmisor>");
                rawXml = Regex.Replace(rawXml, @"<NombreComercial>.*?</NombreComercial>", "<NombreComercial>CERAMIC CHIC</NombreComercial>");

                c.FechaEmision = DateTime.Now.ToString("dd-MM-yyyy");
                c.FechaFirma = DateTime.Now.AddSeconds(secondOffset).ToString("dd-MM-yyyy HH:mm:ss");
                rawXml = Regex.Replace(rawXml, @"<FechaEmision>.*?</FechaEmision>", $"<FechaEmision>{c.FechaEmision}</FechaEmision>");
                rawXml = Regex.Replace(rawXml, @"<FechaHoraFirma>.*?</FechaHoraFirma>", $"<FechaHoraFirma>{c.FechaFirma}</FechaHoraFirma>");

                if (!string.IsNullOrEmpty(c.ReferenciaEncf))
                {
                    rawXml = Regex.Replace(rawXml, @"<NCFModificado>.*?</NCFModificado>", $"<NCFModificado>{c.ReferenciaEncf}</NCFModificado>");
                    rawXml = Regex.Replace(rawXml, @"<FechaNCFModificado>.*?</FechaNCFModificado>", $"<FechaNCFModificado>{c.FechaEmision}</FechaNCFModificado>");
                    rawXml = Regex.Replace(rawXml, @"<IndicadorNotaCredito>.*?</IndicadorNotaCredito>", "<IndicadorNotaCredito>0</IndicadorNotaCredito>");
                }

                c.SignedXml = _signer.SignXml(rawXml, _rncEmisor);
                c.SecurityCode = _signer.ExtractSignatureValue(c.SignedXml).Substring(0, 6);
                ParseTotalsAndItems(c);

                var fileName = $"{_rncEmisor}{c.NewEncf}.xml";
                File.WriteAllText(Path.Combine(xmlsDir, fileName), c.SignedXml, Encoding.UTF8);
            }

            // 1. Send E310000000105
            var e31 = missingCases[0];
            Console.Write($"\nTransmitiendo {e31.NewEncf} ({e31.Description})... ");
            var resp31 = await _transport.SendEcfAsync(e31.SignedXml, $"{_rncEmisor}{e31.NewEncf}.xml");
            e31.TrackId = resp31.TrackId;
            Console.WriteLine($"-> TrackId: {e31.TrackId}");

            // Poll E31
            Console.WriteLine("Verificando aprobación de E31 ante DGII...");
            for (int i = 1; i <= 8; i++)
            {
                await Task.Delay(4000);
                var res = await _transport.ConsultarResultadoAsync(e31.TrackId);
                Console.WriteLine($"  Intento {i}/8: Estado = {res.Estado}");
                if (res.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                {
                    e31.EstadoDgii = "Aceptado";
                    break;
                }
                if (res.Estado?.Equals("Rechazado", StringComparison.OrdinalIgnoreCase) == true)
                {
                    e31.EstadoDgii = "Rechazado";
                    if (res.Mensajes != null) e31.MensajesDgii = string.Join("; ", res.Mensajes.ConvertAll(m => m.Valor));
                    break;
                }
            }

            // 2. Send E340000000103
            var e34 = missingCases[1];
            Console.Write($"\nTransmitiendo Nota {e34.NewEncf} (modifica {e34.ReferenciaEncf})... ");
            var resp34 = await _transport.SendEcfAsync(e34.SignedXml, $"{_rncEmisor}{e34.NewEncf}.xml");
            e34.TrackId = resp34.TrackId;
            Console.WriteLine($"-> TrackId: {e34.TrackId}");

            // Poll E34
            Console.WriteLine("Verificando aprobación de E34 ante DGII...");
            for (int i = 1; i <= 8; i++)
            {
                await Task.Delay(4000);
                var res = await _transport.ConsultarResultadoAsync(e34.TrackId);
                Console.WriteLine($"  Intento {i}/8: Estado = {res.Estado}");
                if (res.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                {
                    e34.EstadoDgii = "Aceptado";
                    break;
                }
                if (res.Estado?.Equals("Rechazado", StringComparison.OrdinalIgnoreCase) == true)
                {
                    e34.EstadoDgii = "Rechazado";
                    if (res.Mensajes != null) e34.MensajesDgii = string.Join("; ", res.Mensajes.ConvertAll(m => m.Valor));
                    break;
                }
            }

            // 3. Generate PDFs
            Console.WriteLine("\nGenerando PDFs oficiales...");
            foreach (var c in missingCases)
            {
                var pdfPath = Path.Combine(pdfsDir, $"{c.NewEncf}.pdf");
                GenerateInvoicePdf(c, pdfPath);
                Console.WriteLine($"  [PDF OK] {c.NewEncf}.pdf");
            }

            Console.WriteLine("\n=========================================================================================================");
            Console.WriteLine("RESULTADOS FINALES DE COMPLETADO:");
            foreach (var c in missingCases)
            {
                Console.WriteLine($"  {c.NewEncf} | {c.EstadoDgii} | {c.MensajesDgii}");
            }
            Console.WriteLine("=========================================================================================================");
        }

        private List<SimulationCase> DefineCases()
        {
            return new List<SimulationCase>
            {
                // 4x E31 (Factura de Crédito Fiscal) - All standard 18% ITBIS, NO selective taxes
                new SimulationCase { OriginalFile = "E310000000005", NewEncf = "E310000000301", TipoEcf = "31", Description = "Crédito Fiscal - Porcelanato Calacatta 60x60", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E310000000005", NewEncf = "E310000000302", TipoEcf = "31", Description = "Crédito Fiscal - Cerámica Muro Blanco", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E310000000007", NewEncf = "E310000000303", TipoEcf = "31", Description = "Crédito Fiscal - Porcelanato Maderado", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E310000000010", NewEncf = "E310000000304", TipoEcf = "31", Description = "Crédito Fiscal - Piso Cerámico Antideslizante", IsBaseInvoice = true },

                // 2x E32 >= 250k (Factura de Consumo >= 250k)
                new SimulationCase { OriginalFile = "E320000000005", NewEncf = "E320000000301", TipoEcf = "32", Description = "Consumo >= 250k (Gran Formato)", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E320000000006", NewEncf = "E320000000302", TipoEcf = "32", Description = "Consumo >= 250k (Revestimiento)", IsBaseInvoice = true },

                // 4x E32 < 250k (Factura de Consumo < 250k)
                new SimulationCase { OriginalFile = "E320000000012", NewEncf = "E320000000303", TipoEcf = "32", Description = "Consumo < 250k (Cerámica Baño)", IsRfce = true },
                new SimulationCase { OriginalFile = "E320000000013", NewEncf = "E320000000304", TipoEcf = "32", Description = "Consumo < 250k (Porcelanato Pulido)", IsRfce = true },
                new SimulationCase { OriginalFile = "E320000000014", NewEncf = "E320000000305", TipoEcf = "32", Description = "Consumo < 250k (Listelos Decorativos)", IsRfce = true },
                new SimulationCase { OriginalFile = "E320000000015", NewEncf = "E320000000306", TipoEcf = "32", Description = "Consumo < 250k (Gris Rústico)", IsRfce = true },

                // 1x E33 (Nota de Débito - Modifica E320000000302)
                new SimulationCase { OriginalFile = "E330000000001", NewEncf = "E330000000301", TipoEcf = "33", Description = "Nota Débito - Intereses por Mora", ReferenciaEncf = "E320000000302", IsNote = true },

                // 2x E34 (Nota de Crédito - Modifican E410000000301 y E410000000302)
                new SimulationCase { OriginalFile = "E340000000015", NewEncf = "E340000000301", TipoEcf = "34", Description = "Nota Crédito - Devolución Material", ReferenciaEncf = "E410000000301", IsNote = true },
                new SimulationCase { OriginalFile = "E340000000015", NewEncf = "E340000000302", TipoEcf = "34", Description = "Nota Crédito - Descuento Comercial", ReferenciaEncf = "E410000000302", IsNote = true },

                // 2x E41 (Compras)
                new SimulationCase { OriginalFile = "E410000000001", NewEncf = "E410000000301", TipoEcf = "41", Description = "Compras Informales - Tarimas Madera", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E410000000010", NewEncf = "E410000000302", TipoEcf = "41", Description = "Compras Informales - Material Embalaje", IsBaseInvoice = true },

                // 2x E43 (Gastos Menores)
                new SimulationCase { OriginalFile = "E430000000010", NewEncf = "E430000000301", TipoEcf = "43", Description = "Gastos Menores - Taller Montacargas", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E430000000012", NewEncf = "E430000000302", TipoEcf = "43", Description = "Gastos Menores - Combustible Planta", IsBaseInvoice = true },

                // 2x E44 (Regímenes Especiales)
                new SimulationCase { OriginalFile = "E440000000007", NewEncf = "E440000000301", TipoEcf = "44", Description = "Régimen Especial - Zona Franca A", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E440000000008", NewEncf = "E440000000302", TipoEcf = "44", Description = "Régimen Especial - Zona Franca B", IsBaseInvoice = true },

                // 2x E45 (Gubernamentales)
                new SimulationCase { OriginalFile = "E450000000001", NewEncf = "E450000000301", TipoEcf = "45", Description = "Gubernamental - DGII Sede Central", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E450000000010", NewEncf = "E450000000302", TipoEcf = "45", Description = "Gubernamental - Ministerio Hacienda", IsBaseInvoice = true },

                // 2x E46 (Exportación)
                new SimulationCase { OriginalFile = "E460000000008", NewEncf = "E460000000301", TipoEcf = "46", Description = "Exportación - Distribuidor Caribe", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E460000000011", NewEncf = "E460000000302", TipoEcf = "46", Description = "Exportación - Porcelanatos San Juan", IsBaseInvoice = true },

                // 2x E47 (Pagos al Exterior)
                new SimulationCase { OriginalFile = "E470000000009", NewEncf = "E470000000301", TipoEcf = "47", Description = "Pago Exterior - Moldes Cerámicos Italia", IsBaseInvoice = true },
                new SimulationCase { OriginalFile = "E470000000010", NewEncf = "E470000000302", TipoEcf = "47", Description = "Pago Exterior - Software Diseño Pisos", IsBaseInvoice = true },
            };
        }

        private string BuildAndSignRfce(SimulationCase c)
        {
            var rawRfce = $@"<?xml version=""1.0"" encoding=""utf-8""?><RFCE><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>32</TipoeCF><eNCF>{c.NewEncf}</eNCF><TipoIngresos>01</TipoIngresos><TipoPago>1</TipoPago></IdDoc><Emisor><RNCEmisor>{_rncEmisor}</RNCEmisor><RazonSocialEmisor>CERAMIC CHIC SRL</RazonSocialEmisor><FechaEmision>{c.FechaEmision}</FechaEmision></Emisor><Comprador><RNCComprador>{c.RncComprador}</RNCComprador><RazonSocialComprador>{c.RazonSocialComprador}</RazonSocialComprador></Comprador><Totales><MontoGravadoTotal>{(c.MontoTotal - c.TotalItbis):0.00}</MontoGravadoTotal><MontoGravadoI1>{(c.MontoTotal - c.TotalItbis):0.00}</MontoGravadoI1><TotalITBIS>{c.TotalItbis:0.00}</TotalITBIS><TotalITBIS1>{c.TotalItbis:0.00}</TotalITBIS1><MontoTotal>{c.MontoTotal:0.00}</MontoTotal></Totales><CodigoSeguridadeCF>{c.SecurityCode}</CodigoSeguridadeCF></Encabezado></RFCE>";
            return _signer.SignXml(rawRfce, _rncEmisor);
        }

        private void ParseTotalsAndItems(SimulationCase c)
        {
            var doc = new XmlDocument();
            doc.LoadXml(c.SignedXml);

            var totalNode = doc.SelectSingleNode("//Totales/MontoTotal");
            if (totalNode != null && decimal.TryParse(totalNode.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var mt))
            {
                c.MontoTotal = mt;
            }

            var itbisNode = doc.SelectSingleNode("//Totales/TotalITBIS");
            if (itbisNode != null && decimal.TryParse(itbisNode.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var ti))
            {
                c.TotalItbis = ti;
            }

            var rncComp = doc.SelectSingleNode("//Comprador/RNCComprador");
            c.RncComprador = rncComp?.InnerText?.Trim() ?? string.Empty;

            var idExt = doc.SelectSingleNode("//Comprador/IdentificadorExtranjero");
            c.IdExtranjero = idExt?.InnerText?.Trim();

            var razonComp = doc.SelectSingleNode("//Comprador/RazonSocialComprador");
            c.RazonSocialComprador = razonComp?.InnerText?.Trim() ?? (c.TipoEcf == "43" ? "GASTOS MENORES" : "CLIENTE FINAL");

            var itemNodes = doc.SelectNodes("//DetallesItems/Item");
            if (itemNodes != null)
            {
                foreach (XmlNode n in itemNodes)
                {
                    var nom = n.SelectSingleNode("NombreItem")?.InnerText ?? "Articulo Cerámico";
                    decimal.TryParse(n.SelectSingleNode("CantidadItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var cant);
                    decimal.TryParse(n.SelectSingleNode("PrecioUnitarioItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var pu);
                    decimal.TryParse(n.SelectSingleNode("MontoItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var mi);
                    c.Items.Add((nom, cant > 0 ? cant : 1, pu > 0 ? pu : mi, mi));
                }
            }
        }

        public void RegenerateAllPdfs()
        {
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("REGENERANDO LAS 25 REPRESENTACIONES IMPRESAS (PDF) OFICIALES - CERAMIC CHIC SRL");
            Console.WriteLine("=========================================================================================================");

            var xmlsDir = Path.Combine(_outputBaseDir, "XMLs");
            var pdfsDir = Path.Combine(_outputBaseDir, "PDFs");
            Directory.CreateDirectory(pdfsDir);

            var xmlFiles = Directory.GetFiles(xmlsDir, "*.xml");
            Array.Sort(xmlFiles);

            int successCount = 0;
            foreach (var xmlPath in xmlFiles)
            {
                var xmlContent = File.ReadAllText(xmlPath, Encoding.UTF8);
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var encf = doc.SelectSingleNode("//IdDoc/eNCF")?.InnerText?.Trim() ?? Path.GetFileNameWithoutExtension(xmlPath).Replace(_rncEmisor, "");
                var tipoEcf = encf.Length >= 3 ? encf.Substring(1, 2) : "31";
                var fechaEmision = doc.SelectSingleNode("//*[local-name()='FechaEmision']")?.InnerText?.Trim() ?? DateTime.Now.ToString("dd-MM-yyyy");
                var fechaFirma = doc.SelectSingleNode("//*[local-name()='FechaHoraFirma']")?.InnerText?.Trim() ?? DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                var sigNode = doc.SelectSingleNode("//*[local-name()='SignatureValue']");
                var secCode = sigNode != null && sigNode.InnerText.Trim().Length >= 6 ? sigNode.InnerText.Trim().Substring(0, 6) : "000000";
                var refEncf = doc.SelectSingleNode("//InformacionReferencia/NCFModificado")?.InnerText?.Trim();

                var c = new SimulationCase
                {
                    NewEncf = encf,
                    TipoEcf = tipoEcf,
                    FechaEmision = fechaEmision,
                    FechaFirma = fechaFirma,
                    SecurityCode = secCode,
                    ReferenciaEncf = refEncf,
                    SignedXml = xmlContent,
                    IsRfce = tipoEcf == "32"
                };

                ParseTotalsAndItems(c);
                c.IsRfce = (tipoEcf == "32" && c.MontoTotal < 250000m);

                var pdfPath = Path.Combine(pdfsDir, $"{encf}.pdf");
                try
                {
                    GenerateInvoicePdf(c, pdfPath);
                    Console.WriteLine($"  [OK] {encf}.pdf generado exitosamente (Tipo: {tipoEcf}, Monto: RD$ {c.MontoTotal:N2}, CodSeg: {secCode})");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [ERROR] {encf}: {ex.Message}");
                }
            }

            Console.WriteLine("=========================================================================================================");
            Console.WriteLine($"GENERACIÓN COMPLETADA: {successCount} de {xmlFiles.Length} PDFs generados en: {pdfsDir}");
            Console.WriteLine("=========================================================================================================");
        }

        private void GenerateInvoicePdf(SimulationCase c, string destinationPath)
        {
            // Build official DGII QR Code URL
            var isRfce = c.IsRfce || (c.TipoEcf == "32" && c.MontoTotal < 250000m);
            string qrUrl;
            if (isRfce)
            {
                qrUrl = $"https://fc.dgii.gov.do/certecf/consultatimbrefc?rncemisor={_rncEmisor}&encf={c.NewEncf}&montototal={c.MontoTotal.ToString("F2", CultureInfo.InvariantCulture)}&codigoseguridad={Uri.EscapeDataString(c.SecurityCode)}";
            }
            else
            {
                var buyerParam = !string.IsNullOrWhiteSpace(c.RncComprador) ? $"&rnccomprador={Uri.EscapeDataString(c.RncComprador)}" : string.Empty;
                qrUrl = $"https://ecf.dgii.gov.do/certecf/consultatimbre?rncemisor={_rncEmisor}" +
                        buyerParam +
                        $"&encf={c.NewEncf}" +
                        $"&fechaemision={c.FechaEmision}" +
                        $"&montototal={c.MontoTotal.ToString("F2", CultureInfo.InvariantCulture)}" +
                        $"&fechafirma={Uri.EscapeDataString(c.FechaFirma)}" +
                        $"&codigoseguridad={Uri.EscapeDataString(c.SecurityCode)}";
            }

            using var qrGen = new QRCodeGenerator();
            using var qrData = qrGen.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            var qrBytes = qrCode.GetGraphic(15);

            // Load company logo from server asset
            byte[]? logoBytes = null;
            var logoCandidates = new[]
            {
                @"C:\Users\Jorge\Desktop\Ecf\scratch\ceramic_chic_logo.png",
                @"C:\Users\Jorge\Desktop\Ecf\scratch\ceramic_logo.png",
                @"C:\Users\Jorge\Desktop\Ecf\Ceramic\ceramic_chic_logo.png"
            };
            foreach (var lp in logoCandidates)
            {
                if (File.Exists(lp))
                {
                    try { logoBytes = File.ReadAllBytes(lp); break; } catch { }
                }
            }

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(15, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(8).FontFamily("Arial").FontColor("#111827"));

                    // 1. ENCABEZADO EN DOS COLUMNAS (Formato idéntico al servidor)
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            // Columna Izquierda: Logo y datos fiscales de CERAMIC CHIC SRL
                            row.RelativeItem(5.8f).Column(c1 =>
                            {
                                if (logoBytes != null && logoBytes.Length > 0)
                                {
                                    c1.Item()
                                        .AlignLeft()
                                        .MaxHeight(20, Unit.Millimetre)
                                        .MaxWidth(45, Unit.Millimetre)
                                        .Image(logoBytes)
                                        .FitArea();

                                    c1.Item().Height(4, Unit.Millimetre);
                                }
                                else
                                {
                                    c1.Item().Text("CERAMIC CHIC SRL").Bold().FontSize(12).FontColor("#0B437D");
                                }

                                c1.Item().Text("CERAMIC CHIC SRL").Bold().FontSize(8.5f);
                                c1.Item().Text("RNC: 133-66469-2").FontSize(8f);
                                c1.Item().Text("Tel.: (809) 472-7676").FontSize(8f);
                                c1.Item().Text("Correo: facturacion@ceramicchic.com.do").FontSize(8f);
                                c1.Item().Text("Ave. Isabel Aguiar No. 269, Herrera").FontSize(8f);
                                c1.Item().Text("Santo Domingo, República Dominicana").FontSize(8f);
                                c1.Item().Text("Sucursal: Sede principal").FontSize(8f).FontColor(Colors.Grey.Medium);
                            });

                            // Columna Derecha: Recuadro Fiscal e-CF
                            row.RelativeItem(4.2f).Border(0.8f, Unit.Point).BorderColor("#C5CAD1").Column(card =>
                            {
                                card.Item().Background("#E5E7EB").PaddingVertical(4).PaddingHorizontal(6)
                                    .AlignCenter().Text(GetDocTypeName(c.TipoEcf)).Bold().FontSize(8.5f).FontColor("#111827");

                                card.Item().Padding(5).Column(body =>
                                {
                                    body.Item().Row(r =>
                                    {
                                        r.AutoItem().Text("e-NCF").Bold().FontSize(8f);
                                        r.RelativeItem().AlignRight().Text(c.NewEncf).Bold().FontFamily("Courier").FontSize(10f).FontColor("#0B437D");
                                    });

                                    body.Item().PaddingTop(2).Row(r =>
                                    {
                                        r.AutoItem().Text("Fecha de emisión").FontSize(8f);
                                        r.RelativeItem().AlignRight().Text(c.FechaEmision).FontSize(8f);
                                    });

                                    body.Item().PaddingTop(2).Row(r =>
                                    {
                                        r.AutoItem().Text("Vence el e-NCF").FontSize(8f);
                                        r.RelativeItem().AlignRight().Text("31/12/2027").FontSize(8f);
                                    });

                                    body.Item().PaddingTop(2).Row(r =>
                                    {
                                        r.AutoItem().Text("RefNumber").FontSize(8f);
                                        r.RelativeItem().AlignRight().Text("—").FontSize(8f);
                                    });

                                    if (!string.IsNullOrEmpty(c.ReferenciaEncf))
                                    {
                                        body.Item().PaddingTop(2).Row(r =>
                                        {
                                            r.AutoItem().Text("NCF Modificado").FontSize(8f);
                                            r.RelativeItem().AlignRight().Text(c.ReferenciaEncf).Bold().FontFamily("Courier").FontSize(8f).FontColor(Colors.Red.Medium);
                                        });
                                    }

                                    body.Item().PaddingTop(2).Row(r =>
                                    {
                                        r.AutoItem().Text("Moneda").FontSize(8f);
                                        r.RelativeItem().AlignRight().Text("DOP (RD$)").FontSize(8f);
                                    });
                                });
                            });
                        });
                    });

                    page.Content().PaddingTop(7).Column(col =>
                    {
                        // 2. Banda de dos recuadros: Facturar a y Condiciones comerciales
                        col.Item().Row(row =>
                        {
                            // Recuadro 1: Facturar a
                            row.RelativeItem().Border(0.5f, Unit.Point).BorderColor("#C5CAD1").Padding(6).Column(box =>
                            {
                                box.Item().Text("Facturar a").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                box.Item().PaddingTop(2).Text(c.RazonSocialComprador).Bold().FontSize(8.5f);

                                string rncDisplay;
                                if (!string.IsNullOrWhiteSpace(c.RncComprador))
                                {
                                    var rncLabel = c.RncComprador.Replace("-", "").Trim().Length == 11
                                        ? "Cédula"
                                        : "RNC / Cédula";
                                    rncDisplay = $"{rncLabel}: {c.RncComprador}";
                                }
                                else if (!string.IsNullOrWhiteSpace(c.IdExtranjero))
                                {
                                    rncDisplay = $"ID Extranjero: {c.IdExtranjero}";
                                }
                                else
                                {
                                    rncDisplay = "RNC / Cédula: —";
                                }
                                box.Item().PaddingTop(1).Text(rncDisplay).FontSize(8f);
                                box.Item().PaddingTop(1).Text("Email: —").FontSize(8f);
                                box.Item().PaddingTop(1).Text("Dirección: Santo Domingo, Rep. Dom.").FontSize(8f);
                            });

                            row.Spacing(8f);

                            // Recuadro 2: Condiciones comerciales
                            row.RelativeItem().Border(0.5f, Unit.Point).BorderColor("#C5CAD1").Padding(6).Column(box =>
                            {
                                box.Item().Row(r =>
                                {
                                    r.AutoItem().Text("Fecha de vencimiento").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                    r.RelativeItem().AlignRight().Text(c.FechaEmision).FontSize(8f);
                                });

                                box.Item().PaddingTop(3).Row(r =>
                                {
                                    r.AutoItem().Text("Condición de pago").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                    r.RelativeItem().AlignRight().Text("Al Contado").FontSize(8f);
                                });

                                box.Item().PaddingTop(3).Row(r =>
                                {
                                    r.AutoItem().Text("Vendedor").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                    r.RelativeItem().AlignRight().Text("Víctor Medina").Bold().FontSize(8f);
                                });
                            });
                        });

                        // 3. Tabla de detalle (Exactamente 5 columnas con estilo del servidor)
                        col.Item().PaddingTop(7).Table(tbl =>
                        {
                            tbl.ColumnsDefinition(cols =>
                            {
                                cols.RelativeColumn(4.6f); // Descripción
                                cols.RelativeColumn(1.3f); // Und.
                                cols.RelativeColumn(1.1f); // Cant.
                                cols.RelativeColumn(1.3f); // Precio
                                cols.RelativeColumn(1.7f); // Valor
                            });

                            tbl.Header(h =>
                            {
                                h.Cell().Background("#F3F4F6").BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .PaddingVertical(4).PaddingHorizontal(3).AlignLeft().Text("Descripción").Bold().FontSize(8f);
                                h.Cell().Background("#F3F4F6").BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .PaddingVertical(4).PaddingHorizontal(3).AlignCenter().Text("Und.").Bold().FontSize(8f);
                                h.Cell().Background("#F3F4F6").BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .PaddingVertical(4).PaddingHorizontal(3).AlignRight().Text("Cant.").Bold().FontSize(8f);
                                h.Cell().Background("#F3F4F6").BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .PaddingVertical(4).PaddingHorizontal(3).AlignRight().Text("Precio").Bold().FontSize(8f);
                                h.Cell().Background("#F3F4F6").BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .PaddingVertical(4).PaddingHorizontal(3).AlignRight().Text("Valor").Bold().FontSize(8f);
                            });

                            if (c.Items.Count == 0)
                            {
                                tbl.Cell().ColumnSpan(5).BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                    .Padding(8).AlignCenter().Text("Sin líneas registradas").FontSize(8f);
                            }
                            else
                            {
                                foreach (var itm in c.Items)
                                {
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignLeft().Text(itm.Nombre).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(2).AlignCenter().Text("UND").FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Cantidad.ToString("N2")).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Precio.ToString("N2")).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Monto.ToString("N2")).Bold().FontSize(8f);
                                }
                            }
                        });

                        // 4. Totales y Bloque Fiscal DGII (QR con Código de Seguridad y Fecha Firma DEBAJO según modelo DGII)
                        col.Item().PaddingTop(10).Row(row =>
                        {
                            // Lado Izquierdo: QR Code DGII y debajo Código de Seguridad + Fecha Firma
                            row.RelativeItem(5.2f).Column(qrCol =>
                            {
                                qrCol.Item().Row(qrRow =>
                                {
                                    qrRow.AutoItem().Height(95).Width(95).Image(qrBytes);
                                });

                                qrCol.Item().PaddingTop(4);
                                qrCol.Item().Text($"Código de Seguridad: {c.SecurityCode}").FontSize(7.5f).Bold();
                                qrCol.Item().Text($"Fecha Firma Digital: {c.FechaFirma}").FontSize(7.5f);
                                qrCol.Item().PaddingTop(3);
                                qrCol.Item().Text("Documento emitido conforme a la normativa e-CF de la Dirección General de Impuestos Internos (DGII).")
                                    .FontSize(6.5f).Italic().FontColor(Colors.Grey.Darken2);
                            });

                            // Lado Derecho: Recuadro de Totales y Monto en Letras
                            row.RelativeItem(4.8f).Column(tCol =>
                            {
                                tCol.Item().Table(t =>
                                {
                                    t.ColumnsDefinition(cols =>
                                    {
                                        cols.RelativeColumn(3.5f);
                                        cols.RelativeColumn(1.0f);
                                        cols.RelativeColumn(2.5f);
                                    });

                                    // Subtotal
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).Text("SUBTOTAL").Bold().FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).AlignCenter().Text("RD$").FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Padding(4).AlignRight().Text((c.MontoTotal - c.TotalItbis).ToString("N2")).FontSize(8f);

                                    // ITBIS
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).Text("ITBIS (18%)").Bold().FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).AlignCenter().Text("RD$").FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Padding(4).AlignRight().Text(c.TotalItbis.ToString("N2")).FontSize(8f);

                                    // TOTAL A PAGAR (Destacado azul #05376E)
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor("#05376E").Background("#05376E").Padding(5).Text("TOTAL A PAGAR").Bold().FontColor(Colors.White).FontSize(9f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor("#05376E").Background("#05376E").Padding(5).AlignCenter().Text("RD$").Bold().FontColor(Colors.White).FontSize(9f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor("#05376E").Background("#05376E").Padding(5).AlignRight().Text(c.MontoTotal.ToString("N2")).Bold().FontColor(Colors.White).FontSize(9.5f);
                                });

                                tCol.Item().PaddingTop(6).Text(NumberToWordsConverter.ToDominicanPesosInWords(c.MontoTotal))
                                    .FontSize(7.5f).Italic().FontColor("#111827");
                            });
                        });
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Página 1 de 1 - Representación Impresa Oficial de Comprobante Fiscal Electrónico (e-CF)").FontSize(8f).FontColor(Colors.Grey.Medium);
                    });
                });
            }).GeneratePdf(destinationPath);
        }

        private static string GetDocTypeName(string tipo)
        {
            return tipo switch
            {
                "31" => "FACTURA DE CRÉDITO FISCAL",
                "32" => "FACTURA DE CONSUMO",
                "33" => "NOTA DE DÉBITO",
                "34" => "NOTA DE CRÉDITO",
                "41" => "COMPROBANTE DE COMPRAS",
                "43" => "COMPROBANTE DE GASTOS MENORES",
                "44" => "REGÍMENES ESPECIALES",
                "45" => "COMPROBANTE GUBERNAMENTAL",
                "46" => "COMPROBANTE DE EXPORTACIÓN",
                "47" => "PAGOS AL EXTERIOR",
                _ => "COMPROBANTE ELECTRÓNICO"
            };
        }
    }
}
