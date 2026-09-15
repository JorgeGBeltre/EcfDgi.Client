using System;
using System.Collections.Generic;

namespace EcfDgii.TestRunner
{
    /// <summary>
    /// Conversor de montos numéricos a texto en letras en pesos dominicanos
    /// siguiendo las normas ortográficas y gramaticales de la Real Academia Española (RAE).
    /// </summary>
    public static class NumberToWordsConverter
    {
        private const decimal MaxSupportedAmount = 999_999_999.99m;

        public static string ToDominicanPesosInWords(decimal amount)
        {
            if (amount < 0m || amount > MaxSupportedAmount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    $"El monto {amount:N2} está fuera del rango permitido (0.00 a {MaxSupportedAmount:N2}).");
            }

            var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (rounded > MaxSupportedAmount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    $"El monto redondeado {rounded:N2} excede el límite máximo permitido ({MaxSupportedAmount:N2}).");
            }

            var entero = (long)Math.Floor(rounded);
            var centavos = (int)Math.Round((rounded - entero) * 100, MidpointRounding.AwayFromZero);
            if (centavos == 100)
            {
                entero += 1;
                centavos = 0;
                if (entero > 999_999_999)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(amount),
                        $"El monto {rounded:N2} excede el límite máximo permitido ({MaxSupportedAmount:N2}).");
                }
            }

            var textoEntero = ConvertIntegerToWords(entero);
            return $"Son: {textoEntero} pesos dominicanos con {centavos:D2}/100";
        }

        private static string ConvertIntegerToWords(long value)
        {
            if (value == 0)
                return "cero";

            var millones = (int)(value / 1_000_000);
            var miles = (int)((value % 1_000_000) / 1_000);
            var unidades = (int)(value % 1_000);

            var partes = new List<string>();

            if (millones > 0)
            {
                if (millones == 1)
                {
                    partes.Add("un millón");
                }
                else
                {
                    partes.Add($"{ConvertGroup(millones, isApocopated: true)} millones");
                }
            }

            if (miles > 0)
            {
                if (miles == 1)
                {
                    partes.Add("mil");
                }
                else
                {
                    partes.Add($"{ConvertGroup(miles, isApocopated: true)} mil");
                }
            }

            if (unidades > 0)
            {
                partes.Add(ConvertGroup(unidades, isApocopated: false));
            }

            return string.Join(" ", partes);
        }

        private static string ConvertGroup(int value, bool isApocopated)
        {
            if (value == 100)
                return "cien";

            var centenas = value / 100;
            var resto = value % 100;

            var centenasTexto = centenas switch
            {
                1 => "ciento",
                2 => "doscientos",
                3 => "trescientos",
                4 => "cuatrocientos",
                5 => "quinientos",
                6 => "seiscientos",
                7 => "setecientos",
                8 => "ochocientos",
                9 => "novecientos",
                _ => ""
            };

            if (resto == 0)
                return centenasTexto;

            var restoTexto = ConvertTwoDigits(resto, isApocopated);

            return string.IsNullOrEmpty(centenasTexto)
                ? restoTexto
                : $"{centenasTexto} {restoTexto}";
        }

        private static string ConvertTwoDigits(int value, bool isApocopated)
        {
            if (value <= 29)
            {
                return value switch
                {
                    1 => isApocopated ? "un" : "uno",
                    2 => "dos",
                    3 => "tres",
                    4 => "cuatro",
                    5 => "cinco",
                    6 => "seis",
                    7 => "siete",
                    8 => "ocho",
                    9 => "nueve",
                    10 => "diez",
                    11 => "once",
                    12 => "doce",
                    13 => "trece",
                    14 => "catorce",
                    15 => "quince",
                    16 => "dieciséis",
                    17 => "diecisiete",
                    18 => "dieciocho",
                    19 => "diecinueve",
                    20 => "veinte",
                    21 => isApocopated ? "veintiún" : "veintiuno",
                    22 => "veintidós",
                    23 => "veintitrés",
                    24 => "veinticuatro",
                    25 => "veinticinco",
                    26 => "veintiséis",
                    27 => "veintisiete",
                    28 => "veintiocho",
                    29 => "veintinueve",
                    _ => ""
                };
            }

            var decenas = value / 10;
            var unidades = value % 10;

            var decenasTexto = decenas switch
            {
                3 => "treinta",
                4 => "cuarenta",
                5 => "cincuenta",
                6 => "sesenta",
                7 => "setenta",
                8 => "ochenta",
                9 => "noventa",
                _ => ""
            };

            if (unidades == 0)
                return decenasTexto;

            var unidadesTexto = unidades == 1
                ? (isApocopated ? "un" : "uno")
                : ConvertTwoDigits(unidades, isApocopated: false);

            return $"{decenasTexto} y {unidadesTexto}";
        }
    }
}
