using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace EcfDgii.Client.Infrastructure.Serialization
{
    public class EcfDecimalXmlWriter : XmlWriter
    {
        private readonly XmlWriter _innerWriter;
        private readonly Stack<string> _elementStack = new Stack<string>();
        
        // List of all tag names representing decimal fields in DGII specifications
        private static readonly HashSet<string> DecimalElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MontoGravadoTotal", "MontoGravadoI1", "MontoGravadoI2", "MontoGravadoI3", 
            "MontoExento", "TotalITBIS", "TotalITBIS1", "TotalITBIS2", "TotalITBIS3", 
            "MontoImpuestoAdicional", "MontoTotal", "MontoNoFacturable", "MontoPeriodo", 
            "MontoPago", "MontoImpuestoSelectivoConsumoEspecifico", "MontoImpuestoSelectivoConsumoAdvalorem", 
            "OtrosImpuestosAdicionales", "MontoSubtotal", "MontoItbis", "Quantity", "UnitPrice", "Amount"
        };

        public EcfDecimalXmlWriter(XmlWriter innerWriter)
        {
            _innerWriter = innerWriter ?? throw new ArgumentNullException(nameof(innerWriter));
        }

        public override WriteState WriteState => _innerWriter.WriteState;

        public override void Flush() => _innerWriter.Flush();

        public override string? LookupPrefix(string ns) => _innerWriter.LookupPrefix(ns);

        public override void WriteBase64(byte[] buffer, int index, int count) => _innerWriter.WriteBase64(buffer, index, count);

        public override void WriteCData(string? text) => _innerWriter.WriteCData(text);

        public override void WriteCharEntity(char ch) => _innerWriter.WriteCharEntity(ch);

        public override void WriteChars(char[] buffer, int index, int count) => _innerWriter.WriteChars(buffer, index, count);

        public override void WriteComment(string? text) => _innerWriter.WriteComment(text);

        public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) => _innerWriter.WriteDocType(name, pubid, sysid, subset);

        public override void WriteEndAttribute() => _innerWriter.WriteEndAttribute();

        public override void WriteEndDocument() => _innerWriter.WriteEndDocument();

        public override void WriteEndElement()
        {
            _innerWriter.WriteEndElement();
            if (_elementStack.Count > 0)
                _elementStack.Pop();
        }

        public override void WriteEntityRef(string name) => _innerWriter.WriteEntityRef(name);

        public override void WriteFullEndElement()
        {
            _innerWriter.WriteFullEndElement();
            if (_elementStack.Count > 0)
                _elementStack.Pop();
        }

        public override void WriteProcessingInstruction(string name, string? text) => _innerWriter.WriteProcessingInstruction(name, text);

        public override void WriteRaw(char[] buffer, int index, int count)
        {
            string currentElement = _elementStack.Count > 0 ? _elementStack.Peek() : string.Empty;
            if (DecimalElements.Contains(currentElement) || string.Equals(currentElement, "TipoIngresos", StringComparison.OrdinalIgnoreCase))
            {
                var data = new string(buffer, index, count);
                if (DecimalElements.Contains(currentElement))
                {
                    if (decimal.TryParse(data, NumberStyles.Any, CultureInfo.InvariantCulture, out var decValue))
                    {
                        var formatted = decValue.ToString("F2", CultureInfo.InvariantCulture);
                        _innerWriter.WriteRaw(formatted.ToCharArray(), 0, formatted.Length);
                        return;
                    }
                }
                else if (string.Equals(currentElement, "TipoIngresos", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(data, out var intValue))
                    {
                        var formatted = intValue.ToString("D2");
                        _innerWriter.WriteRaw(formatted.ToCharArray(), 0, formatted.Length);
                        return;
                    }
                }
            }
            _innerWriter.WriteRaw(buffer, index, count);
        }

        public override void WriteRaw(string data)
        {
            string currentElement = _elementStack.Count > 0 ? _elementStack.Peek() : string.Empty;
            if (data != null)
            {
                if (DecimalElements.Contains(currentElement))
                {
                    if (decimal.TryParse(data, NumberStyles.Any, CultureInfo.InvariantCulture, out var decValue))
                    {
                        _innerWriter.WriteRaw(decValue.ToString("F2", CultureInfo.InvariantCulture));
                        return;
                    }
                }
                else if (string.Equals(currentElement, "TipoIngresos", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(data, out var intValue))
                    {
                        _innerWriter.WriteRaw(intValue.ToString("D2"));
                        return;
                    }
                }
            }
            _innerWriter.WriteRaw(data);
        }

        public override void WriteStartAttribute(string? prefix, string localName, string? ns) => _innerWriter.WriteStartAttribute(prefix, localName, ns);

        public override void WriteStartDocument() => _innerWriter.WriteStartDocument();

        public override void WriteStartDocument(bool standalone) => _innerWriter.WriteStartDocument(standalone);

        public override void WriteStartElement(string? prefix, string localName, string? ns)
        {
            _elementStack.Push(localName);
            _innerWriter.WriteStartElement(prefix, localName, ns);
        }

        public override void WriteString(string? text)
        {
            string currentElement = _elementStack.Count > 0 ? _elementStack.Peek() : string.Empty;
            if (text != null)
            {
                if (DecimalElements.Contains(currentElement))
                {
                    if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var decValue))
                    {
                        _innerWriter.WriteString(decValue.ToString("F2", CultureInfo.InvariantCulture));
                        return;
                    }
                }
                else if (string.Equals(currentElement, "TipoIngresos", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(text, out var intValue))
                    {
                        _innerWriter.WriteString(intValue.ToString("D2"));
                        return;
                    }
                }
            }
            _innerWriter.WriteString(text);
        }

        public override void WriteValue(decimal value)
        {
            string currentElement = _elementStack.Count > 0 ? _elementStack.Peek() : string.Empty;
            if (DecimalElements.Contains(currentElement))
            {
                _innerWriter.WriteString(value.ToString("F2", CultureInfo.InvariantCulture));
            }
            else
            {
                _innerWriter.WriteValue(value);
            }
        }

        public override void WriteValue(int value)
        {
            string currentElement = _elementStack.Count > 0 ? _elementStack.Peek() : string.Empty;
            if (string.Equals(currentElement, "TipoIngresos", StringComparison.OrdinalIgnoreCase))
            {
                _innerWriter.WriteString(value.ToString("D2"));
            }
            else
            {
                _innerWriter.WriteValue(value);
            }
        }

        public override void WriteValue(long value) => _innerWriter.WriteValue(value);
        public override void WriteValue(double value) => _innerWriter.WriteValue(value);
        public override void WriteValue(float value) => _innerWriter.WriteValue(value);
        public override void WriteValue(bool value) => _innerWriter.WriteValue(value);
        public override void WriteValue(string? value) => _innerWriter.WriteValue(value);
        public override void WriteValue(DateTime value) => _innerWriter.WriteValue(value);
        public override void WriteValue(DateTimeOffset value) => _innerWriter.WriteValue(value);

        public override void WriteValue(object value)
        {
            if (value is decimal decValue)
            {
                WriteValue(decValue);
            }
            else if (value is int intValue)
            {
                WriteValue(intValue);
            }
            else
            {
                _innerWriter.WriteValue(value);
            }
        }

        public override void WriteSurrogateCharEntity(char lowChar, char highChar) => _innerWriter.WriteSurrogateCharEntity(lowChar, highChar);

        public override void WriteWhitespace(string? ws) => _innerWriter.WriteWhitespace(ws);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerWriter.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
