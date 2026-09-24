using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EcfDgii.TestRunner
{
    public class WillyChicPdfRunner
    {
        private const string RncEmisor = "101889063";
        private const string RncEmisorFormatted = "101-88906-3";
        private const string RazonSocialEmisor = "WILLY CHIC DOMINICANA SRL";
        private const string TelefonoEmisor = "8099557597";
        private const string CorreoEmisor = "contabilidad@willychicrd.com";
        private const string DireccionEmisor = "Autopista Duarte Km. 13 ½, Parque Industrial Dualas, Nave 5 Pantoja, Santo Domingo";
        private const string SucursalEmisor = "SEDE PRINCIPAL";

        private readonly string _xmlsDir = @"C:\Users\Jorge\Desktop\Willychic\Simulacion_Operaciones_Reales\XMLs";
        private readonly string _pdfsDir = @"C:\Users\Jorge\Desktop\Willychic\Simulacion_Operaciones_Reales\PDFs";
        private readonly string _logoPath = @"C:\Users\Jorge\Desktop\Willychic\Willychic_Logo.png";

        public WillyChicPdfRunner()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public class InvoiceData
        {
            public string Encf { get; set; } = "";
            public string TipoEcf { get; set; } = "";
            public string FechaEmision { get; set; } = "";
            public string FechaEmisionFormatted { get; set; } = "";
            public string FechaFirma { get; set; } = "";
            public string? FechaVencimientoSecuencia { get; set; }
            public string? Terms { get; set; }
            public string? ReferenciaEncf { get; set; }
            public string SecurityCode { get; set; } = "";
            public string RncComprador { get; set; } = "";
            public string? IdExtranjero { get; set; }
            public string RazonSocialComprador { get; set; } = "";
            public decimal MontoTotal { get; set; }
            public decimal TotalItbis { get; set; }
            public bool IsRfce { get; set; }
            public List<(string Nombre, string Unidad, decimal Cantidad, decimal Precio, decimal Monto)> Items { get; set; } = new();
        }

        public void GenerateAll()
        {
            Console.WriteLine("=========================================================================================================");
            Console.WriteLine("GENERANDO 25 REPRESENTACIONES IMPRESAS (PDF) OFICIALES - WILLY CHIC DOMINICANA SRL (101889063)");
            Console.WriteLine("=========================================================================================================");

            if (!Directory.Exists(_xmlsDir))
            {
                Console.WriteLine($"[ERROR] Directorio no encontrado: {_xmlsDir}");
                return;
            }

            Directory.CreateDirectory(_pdfsDir);

            byte[]? logoBytes = null;
            if (File.Exists(_logoPath))
            {
                try { logoBytes = File.ReadAllBytes(_logoPath); } catch { }
            }

            var xmlFiles = Directory.GetFiles(_xmlsDir, "*.xml");
            Array.Sort(xmlFiles);

            int count = 0;
            foreach (var xmlPath in xmlFiles)
            {
                try
                {
                    var data = ParseXml(xmlPath);
                    var pdfPath = Path.Combine(_pdfsDir, $"{data.Encf}.pdf");
                    RenderPdf(data, logoBytes, pdfPath);
                    Console.WriteLine($"  [PDF OK] {data.Encf}.pdf (Tipo {data.TipoEcf} | RD$ {data.MontoTotal:N2} | CodSeg: {data.SecurityCode})");
                    count++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [PDF ERROR] {xmlPath}: {ex.Message}");
                }
            }

            // Create ZIP of all PDFs
            var zipPath = Path.Combine(Path.GetDirectoryName(_pdfsDir)!, "PDFS_Simulacion_101889063.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(_pdfsDir, zipPath);
            Console.WriteLine($"\n[ZIP OK] Archivo ZIP creado: {zipPath}");

            var desktopZip = @"C:\Users\Jorge\Desktop\Ecf\PDFS_Simulacion_101889063.zip";
            if (File.Exists(desktopZip)) File.Delete(desktopZip);
            File.Copy(zipPath, desktopZip);
            Console.WriteLine($"[ZIP OK] Copiado a: {desktopZip}");

            Console.WriteLine("=========================================================================================================");
            Console.WriteLine($"PROCESO COMPLETADO: {count} de {xmlFiles.Length} PDFs generados exitosamente en: {_pdfsDir}");
            Console.WriteLine("=========================================================================================================");
        }

        private InvoiceData ParseXml(string xmlPath)
        {
            var content = File.ReadAllText(xmlPath, Encoding.UTF8);
            var doc = new XmlDocument();
            doc.LoadXml(content);

            var encf = doc.SelectSingleNode("//IdDoc/eNCF")?.InnerText?.Trim() ?? Path.GetFileNameWithoutExtension(xmlPath).Replace(RncEmisor, "");
            var tipoEcf = doc.SelectSingleNode("//IdDoc/TipoeCF")?.InnerText?.Trim() ?? (encf.Length >= 3 ? encf.Substring(1, 2) : "31");

            var fechaEmision = doc.SelectSingleNode("//*[local-name()='FechaEmision']")?.InnerText?.Trim() ?? DateTime.Now.ToString("dd-MM-yyyy");
            var fechaFirma = doc.SelectSingleNode("//*[local-name()='FechaHoraFirma']")?.InnerText?.Trim() ?? DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            var fechaVenc = doc.SelectSingleNode("//IdDoc/FechaVencimientoSecuencia")?.InnerText?.Trim();
            var refEncf = doc.SelectSingleNode("//InformacionReferencia/NCFModificado")?.InnerText?.Trim();

            var sigNode = doc.SelectSingleNode("//*[local-name()='SignatureValue']");
            var secCode = sigNode != null && sigNode.InnerText.Trim().Length >= 6 ? sigNode.InnerText.Trim().Substring(0, 6) : "000000";

            var totalNode = doc.SelectSingleNode("//Totales/MontoTotal");
            decimal.TryParse(totalNode?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var montoTotal);

            var itbisNode = doc.SelectSingleNode("//Totales/TotalITBIS");
            decimal.TryParse(itbisNode?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var totalItbis);

            var rncComp = doc.SelectSingleNode("//Comprador/RNCComprador")?.InnerText?.Trim() ?? "";
            var idExt = doc.SelectSingleNode("//Comprador/IdentificadorExtranjero")?.InnerText?.Trim();
            var razonComp = doc.SelectSingleNode("//Comprador/RazonSocialComprador")?.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(razonComp))
            {
                razonComp = tipoEcf switch
                {
                    "43" => "GASTOS MENORES",
                    "32" => "CONSUMIDOR FINAL",
                    _ => "CLIENTE GENERICO"
                };
            }

            // Date format presentation: dd/MM/yyyy
            string fechaEmisionDisplay = fechaEmision;
            if (DateTime.TryParseExact(fechaEmision, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtEmis))
            {
                fechaEmisionDisplay = dtEmis.ToString("dd/MM/yyyy");
            }

            var data = new InvoiceData
            {
                Encf = encf,
                TipoEcf = tipoEcf,
                FechaEmision = fechaEmision,
                FechaEmisionFormatted = fechaEmisionDisplay,
                FechaFirma = fechaFirma,
                FechaVencimientoSecuencia = fechaVenc,
                ReferenciaEncf = refEncf,
                SecurityCode = secCode,
                RncComprador = rncComp,
                IdExtranjero = idExt,
                RazonSocialComprador = razonComp,
                MontoTotal = montoTotal,
                TotalItbis = totalItbis,
                IsRfce = (tipoEcf == "32" && montoTotal < 250000m)
            };

            var itemNodes = doc.SelectNodes("//DetallesItems/Item");
            if (itemNodes != null)
            {
                foreach (XmlNode n in itemNodes)
                {
                    var nom = n.SelectSingleNode("NombreItem")?.InnerText ?? "Artículo";
                    var und = n.SelectSingleNode("UnidadMedida")?.InnerText ?? "UND";
                    decimal.TryParse(n.SelectSingleNode("CantidadItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var cant);
                    decimal.TryParse(n.SelectSingleNode("PrecioUnitarioItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var pu);
                    decimal.TryParse(n.SelectSingleNode("MontoItem")?.InnerText, NumberStyles.Any, CultureInfo.InvariantCulture, out var mi);
                    data.Items.Add((nom, und, cant > 0 ? cant : 1, pu > 0 ? pu : mi, mi));
                }
            }

            return data;
        }

        private void RenderPdf(InvoiceData c, byte[]? logoBytes, string destinationPath)
        {
            // DGII Official QR Code URL for Certecf (Test / Certificación)
            string qrUrl;
            if (c.IsRfce)
            {
                qrUrl = $"https://fc.dgii.gov.do/certecf/consultatimbrefc?rncemisor={RncEmisor}&encf={c.Encf}&montototal={c.MontoTotal.ToString("F2", CultureInfo.InvariantCulture)}&codigoseguridad={Uri.EscapeDataString(c.SecurityCode)}";
            }
            else
            {
                var buyerParam = !string.IsNullOrWhiteSpace(c.RncComprador) ? $"&rnccomprador={Uri.EscapeDataString(c.RncComprador)}" : string.Empty;
                qrUrl = $"https://ecf.dgii.gov.do/certecf/consultatimbre?rncemisor={RncEmisor}" +
                        buyerParam +
                        $"&encf={c.Encf}" +
                        $"&fechaemision={c.FechaEmision}" +
                        $"&montototal={c.MontoTotal.ToString("F2", CultureInfo.InvariantCulture)}" +
                        $"&fechafirma={Uri.EscapeDataString(c.FechaFirma)}" +
                        $"&codigoseguridad={Uri.EscapeDataString(c.SecurityCode)}";
            }

            // High resolution scannable QR PNG (optimal density 10 px/module with quiet zone)
            byte[] qrBytes;
            using (var qrGen = new QRCodeGenerator())
            using (var qrData = qrGen.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.M))
            using (var qrCode = new PngByteQRCode(qrData))
            {
                qrBytes = qrCode.GetGraphic(10, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, drawQuietZones: true);
            }

            var subtotal = c.MontoTotal - c.TotalItbis;
            var docTypeName = GetDocTypeName(c.TipoEcf);
            var expirationStr = !string.IsNullOrWhiteSpace(c.FechaVencimientoSecuencia)
                ? (DateTime.TryParseExact(c.FechaVencimientoSecuencia, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtVenc) ? dtVenc.ToString("dd/MM/yyyy") : c.FechaVencimientoSecuencia)
                : "31/12/2028";

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(15, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(8).FontFamily("Arial").FontColor("#111827"));

                    // 1. ENCABEZADO EN DOS COLUMNAS
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            // Columna Izquierda: Logo y datos fiscales de WILLY CHIC DOMINICANA SRL
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
                                    c1.Item().Text(RazonSocialEmisor).Bold().FontSize(12).FontColor("#0B437D");
                                }

                                c1.Item().Text(RazonSocialEmisor).Bold().FontSize(8.5f);
                                c1.Item().Text($"RNC: {RncEmisorFormatted}").FontSize(8f);
                                c1.Item().Text($"Tel.: {TelefonoEmisor}").FontSize(8f);
                                c1.Item().Text($"Correo: {CorreoEmisor}").FontSize(8f);
                                c1.Item().Text(DireccionEmisor).FontSize(8f);
                                c1.Item().Text($"Sucursal: {SucursalEmisor}").FontSize(8f).FontColor(Colors.Grey.Medium);
                            });

                            // Columna Derecha: Recuadro Fiscal e-CF
                            row.RelativeItem(4.2f).Border(0.8f, Unit.Point).BorderColor("#C5CAD1").Column(card =>
                            {
                                card.Item().Background("#E5E7EB").PaddingVertical(4).PaddingHorizontal(6)
                                    .AlignCenter().Text(docTypeName).Bold().FontSize(8.5f).FontColor("#111827");

                                card.Item().Padding(5).Column(body =>
                                {
                                    body.Item().Row(r =>
                                    {
                                        r.AutoItem().Text("e-NCF").Bold().FontSize(8f);
                                        r.RelativeItem().AlignRight().Text(c.Encf).Bold().FontFamily("Courier").FontSize(10f).FontColor("#0B437D");
                                    });

                                    body.Item().PaddingTop(2).Row(r =>
                                    {
                                        r.AutoItem().Text("Fecha de emisión").FontSize(8f);
                                        r.RelativeItem().AlignRight().Text(c.FechaEmisionFormatted).FontSize(8f);
                                    });

                                    // Vence el e-NCF: solo para comprobantes con fecha de vencimiento (no 32 ni 34)
                                    if (c.TipoEcf != "32" && c.TipoEcf != "34")
                                    {
                                        body.Item().PaddingTop(2).Row(r =>
                                        {
                                            r.AutoItem().Text("Vence el e-NCF").FontSize(8f);
                                            r.RelativeItem().AlignRight().Text(expirationStr).FontSize(8f);
                                        });
                                    }

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

                                    if (!string.IsNullOrWhiteSpace(c.Terms))
                                    {
                                        body.Item().PaddingTop(2).Row(r =>
                                        {
                                            r.AutoItem().Text("Términos").FontSize(8f);
                                            r.RelativeItem().AlignRight().Text(c.Terms).FontSize(8f);
                                        });
                                    }
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
                                    var rncClean = c.RncComprador.Replace("-", "").Trim();
                                    var rncLabel = rncClean.Length == 11 ? "Cédula" : "RNC / Cédula";
                                    var rncFormatted = rncClean.Length == 9
                                        ? $"{rncClean.Substring(0, 3)}-{rncClean.Substring(3, 5)}-{rncClean.Substring(8, 1)}"
                                        : (rncClean.Length == 11 ? $"{rncClean.Substring(0, 3)}-{rncClean.Substring(3, 7)}-{rncClean.Substring(10, 1)}" : c.RncComprador);
                                    rncDisplay = $"{rncLabel}: {rncFormatted}";
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
                                    r.RelativeItem().AlignRight().Text(c.FechaEmisionFormatted).FontSize(8f);
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

                        // 3. Tabla de detalle (Exactamente 5 columnas con estilo del tenant)
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
                                        .PaddingVertical(3.5f).PaddingHorizontal(2).AlignCenter().Text(itm.Unidad).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Cantidad.ToString("N2")).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Precio.ToString("N2")).FontSize(8f);
                                    tbl.Cell().BorderBottom(0.5f, Unit.Point).BorderColor("#D1D5DB")
                                        .PaddingVertical(3.5f).PaddingHorizontal(3).AlignRight().Text(itm.Monto.ToString("N2")).Bold().FontSize(8f);
                                }
                            }
                        });

                        // 4. Totales y Bloque Fiscal DGII (QR con Código de Seguridad y Fecha Firma)
                        col.Item().PaddingTop(10).Row(row =>
                        {
                            // Lado Izquierdo: QR Code DGII y debajo Código de Seguridad + Fecha Firma
                            row.RelativeItem(5.2f).Column(qrCol =>
                            {
                                qrCol.Item().Row(qrRow =>
                                {
                                    qrRow.AutoItem().Height(95).Width(95).Hyperlink(qrUrl).Image(qrBytes);
                                });

                                qrCol.Item().PaddingTop(4);
                                qrCol.Item().Text($"Código de Seguridad: {c.SecurityCode}").FontSize(7.5f).Bold();
                                qrCol.Item().Text($"Fecha Firma: {c.FechaFirma}").FontSize(7.5f);
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
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Padding(4).AlignRight().Text(subtotal.ToString("N2")).FontSize(8f);

                                    // ITBIS
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).Text("ITBIS").Bold().FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten3).Padding(4).AlignCenter().Text("RD$").FontSize(8f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Padding(4).AlignRight().Text(c.TotalItbis.ToString("N2")).FontSize(8f);

                                    // TOTAL
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten2).Padding(4).Text("TOTAL").Bold().FontSize(8.5f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Background(Colors.Grey.Lighten2).Padding(4).AlignCenter().Text("RD$").Bold().FontSize(8.5f);
                                    t.Cell().Border(0.5f, Unit.Point).BorderColor(Colors.Grey.Medium).Padding(4).AlignRight().Text(c.MontoTotal.ToString("N2")).Bold().FontSize(8.5f);
                                });

                                tCol.Item().PaddingTop(5).Text(NumberToWordsConverter.ToDominicanPesosInWords(c.MontoTotal))
                                    .FontSize(7.5f).Bold().FontColor("#111827");

                                tCol.Item().PaddingTop(2).Text("Documento emitido conforme a la normativa e-CF de la Dirección General de Impuestos Internos (DGII).")
                                    .FontSize(6.5f).Italic().FontColor(Colors.Grey.Darken2);
                            });
                        });
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("Página 1 de 1").FontSize(8f).FontColor(Colors.Grey.Medium);
                    });
                });
            }).GeneratePdf(destinationPath);
        }

        private static string GetDocTypeName(string tipo)
        {
            return tipo switch
            {
                "31" => "Factura de crédito fiscal electrónica",
                "32" => "Factura de consumo electrónica",
                "33" => "Nota de débito electrónica",
                "34" => "Nota de crédito electrónica",
                "41" => "Comprobante de compras electrónico",
                "43" => "Comprobante de gastos menores electrónico",
                "44" => "Comprobante para regímenes especiales electrónico",
                "45" => "Comprobante gubernamental electrónico",
                "46" => "Comprobante de exportación electrónico",
                "47" => "Comprobante para pagos al exterior electrónico",
                _ => "Comprobante fiscal electrónico"
            };
        }
    }
}
