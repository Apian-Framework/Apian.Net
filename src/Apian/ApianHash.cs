using System;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography; // TODO: stop using MD5 hash!!!
using UniLog;

namespace Apian
{
    public static class ApianHash
    {
        // FIXME: Re-add Nethereum to the project and use a REAL hash!!!
        public static string HashString(string input)
        {
            // Create a new Stringbuilder to collect the bytes
            // and create a string.
            StringBuilder sBuilder = new StringBuilder();
            using (MD5 md5Hash = MD5.Create())
            {
                // Convert the input string to a byte array and compute the hash.
                byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
                // Loop through each byte of the hashed data
                // and format each one as a hexadecimal string.
                for (int i = 0; i < data.Length; i++)
                {
                    sBuilder.Append(data[i].ToString("x2"));
                }
            }
            // Return the hexadecimal string.
            return sBuilder.ToString();
        }

    }

}