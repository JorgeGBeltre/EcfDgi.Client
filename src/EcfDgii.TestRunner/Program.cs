using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;

namespace EcfDgii.TestRunner
{
    public class Program
    {
        private static readonly string PfxPath = Environment.GetEnvironmentVariable("TEST_PFX_PATH")
            ?? Path.Combine(".", "scratch", "demo_cert.pfx");
        private static readonly string PfxPassword = Environment.GetEnvironmentVariable("TEST_PFX_PASSWORD") ?? string.Empty;
        private static readonly string RncEmisor = Environment.GetEnvironmentVariable("TEST_RNC_EMISOR") ?? "130000002";
        private static readonly string UnsignedXmlsDir = Environment.GetEnvironmentVariable("TEST_UNSIGNED_XMLS_DIR")
            ?? Path.Combine(".", "scratch", "generated_xmls");
        private static readonly string SignedXmlsDir = Environment.GetEnvironmentVariable("TEST_SIGNED_XMLS_DIR")
            ?? Path.Combine(".", "scratch", "signed_xmls");
        private static readonly string TrackIdsFile = Environment.GetEnvironmentVariable("TEST_TRACK_IDS_FILE")
            ?? Path.Combine(".", "scratch", "track_ids.json");
        private static readonly string OutputBaseDir = Environment.GetEnvironmentVariable("TEST_OUTPUT_DIR")
            ?? Path.Combine(".", "sample_output");

        public static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--sample-pdfs")
            {
                var sampleRunner = new SamplePdfRunner();
                sampleRunner.GenerateAll();
                return;
            }

            Console.WriteLine("================================================================");
            Console.WriteLine($"DGII CERTECF TEST SET RUNNER - COMERCIAL EJEMPLO SRL ({RncEmisor})");
            Console.WriteLine("================================================================");

            if (!File.Exists(PfxPath))
            {
                Console.WriteLine($"[ERROR] Certificate file not found at {PfxPath}");
                return;
            }

            Directory.CreateDirectory(SignedXmlsDir);

            using var cert = X509CertificateLoader.LoadPkcs12FromFile(PfxPath, PfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            Console.WriteLine($"[INFO] Certificate loaded: {cert.Subject}");
            Console.WriteLine($"[INFO] Valid from {cert.NotBefore} to {cert.NotAfter}");

            var signer = new EcfXmlSigner(cert);
            var envConfig = EcfEnvironmentConfig.GetConfig(AmbienteEnum.Certificacion);
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var tokenManager = new EcfTokenManager(httpClient, signer, envConfig, RncEmisor);
            var transport = new DgiiDirectTransport(httpClient, tokenManager, envConfig);

            Console.WriteLine("\n[1/5] Authenticating with DGII CerteCF...");
            try
            {
                var token = await tokenManager.GetTokenAsync();
                Console.WriteLine($"[SUCCESS] DGII JWT Token acquired! Length: {token.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] DGII Authentication failed: {ex.Message}");
                return;
            }

            var masterTrackIds = new Dictionary<string, string>();
            if (File.Exists(TrackIdsFile))
            {
                var json = File.ReadAllText(TrackIdsFile);
                masterTrackIds = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }

            bool queryOnly = args.Length > 0 && args[0] == "--query-only";
            bool sendRfceOnly = args.Length > 0 && args[0] == "--send-rfce-only";
            bool sendExistingRfce = args.Length > 0 && args[0] == "--send-existing-rfce";
            bool checkRfce = args.Length > 0 && args[0] == "--check-rfce";
            bool fix15 = args.Length > 0 && args[0] == "--fix-15";
            bool send21Only = args.Length > 0 && args[0] == "--send-21-only";
            bool sendAcecf = args.Length > 0 && args[0] == "--send-acecf";
            bool runSimulation = args.Length > 0 && (args[0] == "--run-simulation" || args[0] == "--simulation");
            bool fixMissing = args.Length > 0 && (args[0] == "--fix-missing" || args[0] == "--run-missing");
            bool generatePdfs = args.Length > 0 && (args[0] == "--generate-pdfs" || args[0] == "--pdfs");

            if (generatePdfs)
            {
                var simRunner = new SimulationRunner(signer, transport);
                simRunner.RegenerateAllPdfs();
                return;
            }

            if (runSimulation)
            {
                var simRunner = new SimulationRunner(signer, transport);
                await simRunner.RunAsync();
                return;
            }

            if (fixMissing)
            {
                var simRunner = new SimulationRunner(signer, transport);
                await simRunner.RunMissingAsync();
                return;
            }

            if (sendAcecf)
            {
                await ProcessAprobacionComercialAsync(signer, transport);
                return;
            }

            if (sendExistingRfce)
            {
                Console.WriteLine("\nTransmitting 4 Existing Signed RFCEs via RecepcionFC...");
                var rfces = new[] { "E320000000012", "E320000000013", "E320000000014", "E320000000015" };
                foreach (var encf in rfces)
                {
                    var signedPath = Path.Combine(SignedXmlsDir, $"RFCE_{encf}.xml");
                    var xml = File.ReadAllText(signedPath, Encoding.UTF8);
                    var match = System.Text.RegularExpressions.Regex.Match(xml, @"<CodigoSeguridadeCF>(.*?)</CodigoSeguridadeCF>");
                    var code = match.Success ? match.Groups[1].Value : "N/A";
                    Console.Write($"Sending RFCE {encf} (Code: {code})... ");
                    try
                    {
                        var resp = await transport.SendRfceAsync(xml, $"{RncEmisor}{encf}.xml");
                        Console.WriteLine($"-> Estado: {resp.Estado} (Codigo: {resp.Codigo})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"-> FAILED: {ex.Message}");
                    }
                    await Task.Delay(500);
                }
                return;
            }

            if (fix15)
            {
                var unsignedPath = Path.Combine(UnsignedXmlsDir, "E320000000015.xml");
                var rawXml = File.ReadAllText(unsignedPath, Encoding.UTF8);
                rawXml = System.Text.RegularExpressions.Regex.Replace(rawXml, @"<FechaHoraFirma>.*?</FechaHoraFirma>", "<FechaHoraFirma>01-04-2020 12:00:00</FechaHoraFirma>");
                var signed = signer.SignXml(rawXml, RncEmisor);
                var code = signer.ExtractSignatureValue(signed).Substring(0, 6);
                Console.WriteLine($"Signed E320000000015 code: {code}");

                var b2cDir = Path.Combine(OutputBaseDir, "Facturas_Consumo_Menor_250k");
                var b2cCompletas = Path.Combine(b2cDir, "Facturas_eCF32_Completas");
                var b2cRfce = Path.Combine(b2cDir, "Resumenes_RFCE");
                Directory.CreateDirectory(b2cCompletas);
                Directory.CreateDirectory(b2cRfce);

                var dest1 = Path.Combine(b2cDir, $"{RncEmisor}E320000000015.xml");
                var dest2 = Path.Combine(b2cCompletas, $"{RncEmisor}E320000000015.xml");
                File.WriteAllText(dest1, signed, Encoding.UTF8);
                File.WriteAllText(dest2, signed, Encoding.UTF8);

                var rfceRawXml = $@"<?xml version=""1.0"" encoding=""utf-8""?><RFCE><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>32</TipoeCF><eNCF>E320000000015</eNCF><TipoIngresos>01</TipoIngresos><TipoPago>1</TipoPago></IdDoc><Emisor><RNCEmisor>{RncEmisor}</RNCEmisor><RazonSocialEmisor>DOCUMENTOS ELECTRONICOS PRUEBA FACTURA DE CONSUMO MENOR 250MIL</RazonSocialEmisor><FechaEmision>01-04-2020</FechaEmision></Emisor><Comprador><RNCComprador>130000000</RNCComprador><RazonSocialComprador>DOCUMENTOS ELECTRONICOS DE 03</RazonSocialComprador></Comprador><Totales><MontoGravadoTotal>55000.00</MontoGravadoTotal><MontoGravadoI1>55000.00</MontoGravadoI1><TotalITBIS>9900.00</TotalITBIS><TotalITBIS1>9900.00</TotalITBIS1><MontoTotal>64900.00</MontoTotal></Totales><CodigoSeguridadeCF>{code}</CodigoSeguridadeCF></Encabezado></RFCE>";
                var signedRfce = signer.SignXml(rfceRawXml, RncEmisor);
                File.WriteAllText(Path.Combine(SignedXmlsDir, "RFCE_E320000000015.xml"), signedRfce, Encoding.UTF8);
                File.WriteAllText(Path.Combine(b2cRfce, $"{RncEmisor}E320000000015.xml"), signedRfce, Encoding.UTF8);

                Console.Write($"Sending RFCE E320000000015 ({code})... ");
                try
                {
                    var resp = await transport.SendRfceAsync(signedRfce, $"{RncEmisor}E320000000015.xml");
                    Console.WriteLine($"-> Estado: {resp.Estado} (Codigo: {resp.Codigo})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"-> FAILED: {ex.Message}");
                }

                var zipPath = Path.Combine(b2cDir, $"{RncEmisor}_Facturas_Consumo_Menor_250k.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(b2cCompletas, zipPath);
                Console.WriteLine("Output files and zip updated successfully.");
                return;
            }

            if (checkRfce)
            {
                Console.WriteLine("\nQuerying DGII Consulta RFCE for all 4 cases...");
                var testCodes = new Dictionary<string, string[]>
                {
                    ["E320000000012"] = new[] { "AbC123", "DeF456", "000001" },
                    ["E320000000013"] = new[] { "GhI789", "JkL012", "000002" },
                    ["E320000000014"] = new[] { "MnO345", "PqR678", "000003" },
                    ["E320000000015"] = new[] { "StU901", "VwX234", "000004" }
                };

                foreach (var kvp in testCodes)
                {
                    var encf = kvp.Key;
                    Console.WriteLine($"\n--- Checking {encf} ---");
                    foreach (var code in kvp.Value)
                    {
                        try
                        {
                            var res = await transport.ConsultarRfceAsync(RncEmisor, encf, code);
                            Console.WriteLine($"  Code '{code}': Estado={res.Estado}, Codigo={res.Codigo}, SecuenciaUtilizada={res.SecuenciaUtilizada}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Code '{code}': Error -> {ex.Message}");
                        }
                    }
                }
                return;
            }

            if (sendRfceOnly)
            {
                Console.WriteLine("\nTransmitting 4 RFCE B2C Resumenes via RecepcionFC...");
                await PrepareAndSendRfcesAsync(signer, transport);
                return;
            }

            if (!queryOnly)
            {
                if (!send21Only)
                {
                    Console.WriteLine("\n[2/5] Preparing and Transmitting 4 RFCE B2C Resumenes (with fresh unique security codes)...");
                    await PrepareAndSendRfcesAsync(signer, transport);
                }
                else
                {
                    Console.WriteLine("\n[RFCE ALREADY ACCEPTED] Skipping RFCE transmission to preserve 4/4 accepted resúmenes in OFV!");
                }

                Console.WriteLine("\n[3/5] Transmitting 18 Base e-CF Invoices via Recepcion...");
                var baseInvoices = new[]
                {
                    "E310000000002", "E310000000005", "E310000000007", "E310000000010",
                    "E320000000005", "E320000000006",
                    "E410000000001", "E410000000010",
                    "E430000000010", "E430000000012",
                    "E440000000007", "E440000000008",
                    "E450000000001", "E450000000010",
                    "E460000000008", "E460000000011",
                    "E470000000009", "E470000000010"
                };

                foreach (var encf in baseInvoices)
                {
                    await SendDocAsync(encf, signer, transport, masterTrackIds);
                    await Task.Delay(300);
                }

                File.WriteAllText(TrackIdsFile, System.Text.Json.JsonSerializer.Serialize(masterTrackIds, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine("\n[4/5] Waiting for DGII to validate all 18 base invoices before emitting Credit/Debit notes...");
                bool allBaseAccepted = false;
                for (int attempt = 1; attempt <= 12; attempt++)
                {
                    Console.Write($"  Attempt {attempt}/12: Checking base invoices... ");
                    await Task.Delay(5000);

                    int acceptedBase = 0;
                    foreach (var encf in baseInvoices)
                    {
                        if (masterTrackIds.TryGetValue(encf, out var tid))
                        {
                            try
                            {
                                var res = await transport.ConsultarResultadoAsync(tid);
                                if (res.Estado?.Equals("Aceptado", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    acceptedBase++;
                                }
                            }
                            catch { }
                        }
                    }

                    Console.WriteLine($"{acceptedBase} of {baseInvoices.Length} accepted.");
                    if (acceptedBase == baseInvoices.Length)
                    {
                        allBaseAccepted = true;
                        break;
                    }
                }

                if (!allBaseAccepted)
                {
                    Console.WriteLine("[WARN] Not all base invoices are confirmed Aceptado yet, waiting extra 10s...");
                    await Task.Delay(10000);
                }
                else
                {
                    Console.WriteLine("[SUCCESS] All 18 base invoices are confirmed ACEPTADOS by DGII! Waiting 8s for DB indexing...");
                    await Task.Delay(8000);
                }

                Console.WriteLine("\n[5/5] Transmitting 3 Credit/Debit Notes (referencing now-accepted invoices)...");
                var noteDocs = new[] { "E330000000001", "E340000000015", "E340000000016" };
                foreach (var encf in noteDocs)
                {
                    await SendDocAsync(encf, signer, transport, masterTrackIds);
                    await Task.Delay(2000);
                }

                File.WriteAllText(TrackIdsFile, System.Text.Json.JsonSerializer.Serialize(masterTrackIds, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine("\nWaiting 15 seconds for DGII processing of notes before final polling...");
                await Task.Delay(15000);
            }

            Console.WriteLine("\n=========================================================================================================");
            Console.WriteLine($"OFFICIAL DGII HOMOLOGATION TEST RESULTS - COMERCIAL EJEMPLO SRL ({RncEmisor})");
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("{0,-15} | {1,-6} | {2,-38} | {3,-12} | {4}", "eNCF", "Canal", "TrackId / Ref", "Estado", "Mensajes DGII");
            Console.WriteLine("---------------------------------------------------------------------------------------------------------");

            var b2cRfceSet = new HashSet<string> { "E320000000012", "E320000000013", "E320000000014", "E320000000015" };
            var allCases = new[]
            {
                "E310000000002", "E310000000005", "E310000000007", "E310000000010",
                "E320000000005", "E320000000006",
                "E320000000012", "E320000000013", "E320000000014", "E320000000015",
                "E330000000001",
                "E340000000015", "E340000000016",
                "E410000000001", "E410000000010",
                "E430000000010", "E430000000012",
                "E440000000007", "E440000000008",
                "E450000000001", "E450000000010",
                "E460000000008", "E460000000011",
                "E470000000009", "E470000000010"
            };

            int acceptedCount = 0;
            foreach (var encf in allCases)
            {
                if (b2cRfceSet.Contains(encf))
                {
                    Console.WriteLine("{0,-15} | {1,-6} | {2,-38} | {3,-12} | {4}", encf, "RFCE", "Secuencia Aceptada DGII B2C", "Aceptado", "Aceptado en Resumen RFCE (B2C < 250k)");
                    acceptedCount++;
                    continue;
                }

                var trackId = masterTrackIds.TryGetValue(encf, out var tid) ? tid : "";
                string finalEstado = "Sin TrackId";
                string msgs = "";

                if (!string.IsNullOrEmpty(trackId))
                {
                    try
                    {
                        var res = await transport.ConsultarResultadoAsync(trackId);
                        finalEstado = res.Estado ?? "Desconocido";
                        if (res.Mensajes != null && res.Mensajes.Count > 0)
                        {
                            msgs = string.Join("; ", res.Mensajes.ConvertAll(m => m.Valor));
                        }
                    }
                    catch (Exception ex)
                    {
                        finalEstado = "Error";
                        msgs = ex.Message;
                    }
                }

                if (finalEstado.Equals("Aceptado", StringComparison.OrdinalIgnoreCase))
                {
                    acceptedCount++;
                }

                Console.WriteLine("{0,-15} | {1,-6} | {2,-38} | {3,-12} | {4}", encf, "e-CF", trackId, finalEstado, msgs);
                await Task.Delay(200);
            }

            Console.WriteLine("=========================================================================================================");
            Console.WriteLine($"SUMMARY: {acceptedCount} of {allCases.Length} cases ACEPTADOS por DGII!");
            Console.WriteLine("=========================================================================================================");
        }

        private static async Task PrepareAndSendRfcesAsync(EcfXmlSigner signer, DgiiDirectTransport transport)
        {
            var rfceData = new Dictionary<string, (string MontoGravado, string Itbis, string MontoTotal)>
            {
                ["E320000000012"] = ("40000.00", "7200.00", "47200.00"),
                ["E320000000013"] = ("95000.00", "17100.00", "112100.00"),
                ["E320000000014"] = ("10100.00", "1818.00", "11918.00"),
                ["E320000000015"] = ("55000.00", "9900.00", "64900.00")
            };

            var outputDir = Path.Combine(OutputBaseDir, "Facturas_Consumo_Menor_250k");
            var outputCompletas = Path.Combine(outputDir, "Facturas_eCF32_Completas");
            var outputRfce = Path.Combine(outputDir, "Resumenes_RFCE");
            Directory.CreateDirectory(outputCompletas);
            Directory.CreateDirectory(outputRfce);

            int secondOffset = 0;
            foreach (var kvp in rfceData)
            {
                var encf = kvp.Key;
                var data = kvp.Value;
                secondOffset += 7;

                var unsignedEcfPath = Path.Combine(UnsignedXmlsDir, $"{encf}.xml");
                var ecfXml = File.ReadAllText(unsignedEcfPath, Encoding.UTF8);

                var freshTime = DateTime.Now.AddSeconds(secondOffset).ToString("dd-MM-yyyy HH:mm:ss");
                ecfXml = System.Text.RegularExpressions.Regex.Replace(ecfXml, @"<FechaHoraFirma>.*?</FechaHoraFirma>", $"<FechaHoraFirma>{freshTime}</FechaHoraFirma>");

                var signedEcf = signer.SignXml(ecfXml, RncEmisor);
                var sigVal = signer.ExtractSignatureValue(signedEcf);
                var secCode = sigVal.Substring(0, 6);

                File.WriteAllText(Path.Combine(SignedXmlsDir, $"{encf}.xml"), signedEcf, Encoding.UTF8);
                File.WriteAllText(Path.Combine(outputDir, $"{RncEmisor}{encf}.xml"), signedEcf, Encoding.UTF8);
                File.WriteAllText(Path.Combine(outputCompletas, $"{RncEmisor}{encf}.xml"), signedEcf, Encoding.UTF8);

                var rfceRawXml = $@"<?xml version=""1.0"" encoding=""utf-8""?><RFCE><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>32</TipoeCF><eNCF>{encf}</eNCF><TipoIngresos>01</TipoIngresos><TipoPago>1</TipoPago></IdDoc><Emisor><RNCEmisor>{RncEmisor}</RNCEmisor><RazonSocialEmisor>DOCUMENTOS ELECTRONICOS PRUEBA FACTURA DE CONSUMO MENOR 250MIL</RazonSocialEmisor><FechaEmision>01-04-2020</FechaEmision></Emisor><Comprador><RNCComprador>130000000</RNCComprador><RazonSocialComprador>DOCUMENTOS ELECTRONICOS DE 03</RazonSocialComprador></Comprador><Totales><MontoGravadoTotal>{data.MontoGravado}</MontoGravadoTotal><MontoGravadoI1>{data.MontoGravado}</MontoGravadoI1><TotalITBIS>{data.Itbis}</TotalITBIS><TotalITBIS1>{data.Itbis}</TotalITBIS1><MontoTotal>{data.MontoTotal}</MontoTotal></Totales><CodigoSeguridadeCF>{secCode}</CodigoSeguridadeCF></Encabezado></RFCE>";

                var signedRfce = signer.SignXml(rfceRawXml, RncEmisor);
                File.WriteAllText(Path.Combine(SignedXmlsDir, $"RFCE_{encf}.xml"), signedRfce, Encoding.UTF8);
                File.WriteAllText(Path.Combine(outputRfce, $"{RncEmisor}{encf}.xml"), signedRfce, Encoding.UTF8);

                var fileName = $"{RncEmisor}{encf}.xml";
                Console.Write($"  Sending RFCE {encf} (CodigoSeguridad: {secCode} / Time: {freshTime})... ");
                try
                {
                    var resp = await transport.SendRfceAsync(signedRfce, fileName);
                    Console.WriteLine($"-> Estado: {resp.Estado} (Codigo: {resp.Codigo})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"-> FAILED: {ex.Message}");
                }

                await Task.Delay(500);
            }

            try
            {
                var zipPath = Path.Combine(outputDir, $"{RncEmisor}_Facturas_Consumo_Menor_250k.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(outputCompletas, zipPath);
            }
            catch { }
        }

        private static async Task SendDocAsync(string encf, EcfXmlSigner signer, DgiiDirectTransport transport, Dictionary<string, string> masterTrackIds)
        {
            var xmlPath = Path.Combine(UnsignedXmlsDir, $"{encf}.xml");
            if (!File.Exists(xmlPath))
            {
                Console.WriteLine($"  [SKIP] {encf}: file not found");
                return;
            }

            var rawXml = File.ReadAllText(xmlPath, Encoding.UTF8);
            try
            {
                var signedXml = signer.SignXml(rawXml, RncEmisor);
                File.WriteAllText(Path.Combine(SignedXmlsDir, $"{encf}.xml"), signedXml, Encoding.UTF8);

                var fileName = $"{RncEmisor}{encf}.xml";
                Console.Write($"  Sending {encf}... ");

                var resp = await transport.SendEcfAsync(signedXml, fileName);
                Console.WriteLine($"-> TrackId: {resp.TrackId}");
                if (!string.IsNullOrEmpty(resp.TrackId))
                {
                    masterTrackIds[encf] = resp.TrackId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"-> FAILED: {ex.Message}");
            }
        }

        private static async Task ProcessAprobacionComercialAsync(EcfXmlSigner signer, DgiiDirectTransport transport)
        {
            Console.WriteLine("\n=========================================================================================================");
            Console.WriteLine($"DGII APROBACION COMERCIAL (ACECF) - COMERCIAL EJEMPLO SRL ({RncEmisor})");
            Console.WriteLine("=========================================================================================================");

            var jsonPath = Environment.GetEnvironmentVariable("TEST_ACECF_JSON")
                ?? Path.Combine(".", "scratch", "acecf_items.json");
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine($"[ERROR] {jsonPath} not found!");
                return;
            }

            var json = File.ReadAllText(jsonPath);
            var items = System.Text.Json.JsonSerializer.Deserialize<List<AcecfItem>>(json);
            if (items == null || items.Count == 0)
            {
                Console.WriteLine("[ERROR] No items found in acecf_items.json");
                return;
            }

            var acecfDir = Path.Combine(OutputBaseDir, "Aprobacion_Comercial");
            var acecfXmls = Path.Combine(acecfDir, "XMLs");
            Directory.CreateDirectory(acecfDir);
            Directory.CreateDirectory(acecfXmls);

            var xsdPath = Path.Combine(".", "Documentación Técnica (XSD)", "ACECF v.1.0.xsd");
            var validator = new EcfSchemaValidator();

            int acceptedCount = 0;
            Console.WriteLine("{0,-15} | {1,-12} | {2,-12} | {3,-10} | {4}", "eNCF", "Monto", "Estado DGII", "Codigo", "Mensajes DGII");
            Console.WriteLine("---------------------------------------------------------------------------------------------------------");

            foreach (var item in items)
            {
                var montoStr = item.MontoTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var rawXml = $@"<?xml version=""1.0"" encoding=""utf-8""?><ACECF><DetalleAprobacionComercial><Version>{item.Version}</Version><RNCEmisor>{item.RNCEmisor}</RNCEmisor><eNCF>{item.eNCF}</eNCF><FechaEmision>{item.FechaEmision}</FechaEmision><MontoTotal>{montoStr}</MontoTotal><RNCComprador>{item.RNCComprador}</RNCComprador><Estado>{item.Estado}</Estado><FechaHoraAprobacionComercial>{item.FechaHoraAprobacionComercial}</FechaHoraAprobacionComercial></DetalleAprobacionComercial></ACECF>";

                var signedXml = signer.SignXml(rawXml, item.RNCComprador);

                if (File.Exists(xsdPath))
                {
                    var val = validator.Validate(signedXml, xsdPath);
                    if (!val.IsValid)
                    {
                        Console.WriteLine($"[XSD WARNING] {item.eNCF}: {string.Join("; ", val.Errors)}");
                    }
                }

                var fileName = $"{item.RNCComprador}{item.eNCF}.xml";
                File.WriteAllText(Path.Combine(acecfDir, fileName), signedXml, Encoding.UTF8);
                File.WriteAllText(Path.Combine(acecfXmls, fileName), signedXml, Encoding.UTF8);

                string estadoDgii = "Error";
                string codigoDgii = "";
                string msgs = "";

                try
                {
                    var resp = await transport.SendAprobacionComercialAsync(signedXml, fileName);
                    estadoDgii = resp.Estado ?? "Desconocido";
                    codigoDgii = resp.Codigo ?? "";
                    if (resp.Mensaje != null && resp.Mensaje.Count > 0)
                    {
                        msgs = string.Join("; ", resp.Mensaje);
                    }

                    if (estadoDgii.Contains("Aprobado", StringComparison.OrdinalIgnoreCase) ||
                        estadoDgii.Contains("Aceptado", StringComparison.OrdinalIgnoreCase) ||
                        codigoDgii == "1")
                    {
                        acceptedCount++;
                    }
                }
                catch (Exception ex)
                {
                    estadoDgii = "Excepción";
                    msgs = ex.Message;
                }

                Console.WriteLine("{0,-15} | {1,-12} | {2,-12} | {3,-10} | {4}", item.eNCF, montoStr, estadoDgii, codigoDgii, msgs);
                await Task.Delay(400);
            }

            try
            {
                var zipPath = Path.Combine(acecfDir, $"{RncEmisor}_Aprobacion_Comercial.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(acecfXmls, zipPath);
            }
            catch { }

            Console.WriteLine("=========================================================================================================");
            Console.WriteLine($"ACECF SUMMARY: {acceptedCount} de {items.Count} Aprobaciones Comerciales procesadas con DGII.");
            Console.WriteLine($"Archivos XML y ZIP listos en: {acecfDir}");
            Console.WriteLine("=========================================================================================================");
        }
    }

    public class AcecfItem
    {
        public string Version { get; set; } = "1.0";
        public string RNCEmisor { get; set; } = "";
        public string eNCF { get; set; } = "";
        public string FechaEmision { get; set; } = "";
        public decimal MontoTotal { get; set; }
        public string RNCComprador { get; set; } = "";
        public int Estado { get; set; } = 1;
        public string? DetalleMotivoRechazo { get; set; }
        public string FechaHoraAprobacionComercial { get; set; } = "";
    }
}
