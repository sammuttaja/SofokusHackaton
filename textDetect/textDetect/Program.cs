using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Google.Cloud.Vision.V1;
using Google.Apis.Auth.OAuth2;
using Grpc.Auth;
using Google.Cloud.Language.V1;
using Google.Protobuf;
using Google.Cloud.Translation.V2;
using Google.Cloud.Storage.V1;
using PdfSharp.Pdf;
using System.Text.RegularExpressions;

namespace textDetect
{
    class Program
    {
        static void Main(string[] args)
        {
            var credential = GoogleCredential.FromFile("My First Project-ce4c6438e599.json");
            Grpc.Core.Channel channel = new Grpc.Core.Channel(ImageAnnotatorClient.DefaultEndpoint.Host,
                ImageAnnotatorClient.DefaultEndpoint.Port, credential.ToChannelCredentials());
            ImageAnnotatorClient client = ImageAnnotatorClient.Create(channel);


            string[] parset = args[0].Split('.');
            
            /*if (!File.Exists(parset[0] + ".pdf"))
            {
                using (PdfDocument document = new PdfDocument())
                {
                    PdfPage page = document.AddPage();
                    using(PdfSharp.Drawing.XImage img = PdfSharp.Drawing.XImage.FromFile(args[0]))
                    {
                        int height = (int)(((double)img.Size.Width / (double)img.Size.Height) * img.PixelHeight);
                        page.Width = img.Size.Width;
                        page.Height = height;

                        PdfSharp.Drawing.XGraphics gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                        gfx.DrawImage(img, 0, 0, page.Width, height);
                    }
                    document.Save(parset[0] + ".pdf");
                }
            }*/
            if (parset[1].Contains("pdf"))
            {
                var asyncRequest = new AsyncAnnotateFileRequest
                {
                    InputConfig = new InputConfig
                    {
                        GcsSource = new GcsSource
                        {
                            Uri = "gs://kuittei/laskumalli.pdf"
                        },
                        MimeType = "application/pdf"
                    },
                    OutputConfig = new OutputConfig
                    {
                        BatchSize = 2,
                        GcsDestination = new GcsDestination
                        {
                            Uri = "gs://kuittei/kuittiTulos.json"
                        }
                    }
                };

                asyncRequest.Features.Add(new Feature
                {
                    Type = Feature.Types.Type.DocumentTextDetection
                });

                List<AsyncAnnotateFileRequest> requests = new List<AsyncAnnotateFileRequest>
                {
                    asyncRequest
                };

                var operation = client.AsyncBatchAnnotateFiles(requests);

                operation.PollUntilCompleted();

                var storageClient = StorageClient.Create(credential);

                var blobList = storageClient.ListObjects("kuittei", "kuittiTulos.json");

                var output = blobList.Where(x => x.Name.Contains(".json")).First();

                var jsonString = "";
                using (var stream = new MemoryStream())
                {
                    storageClient.DownloadObject(output, stream);
                    jsonString = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }

                var response = JsonParser.Default.Parse<AnnotateFileResponse>(jsonString);

                var firstPAgeREsponse = response.Responses[0];
                var annotation = firstPAgeREsponse.FullTextAnnotation;
                Console.WriteLine(annotation.Text);
                Regex rx = new Regex(@"[a-zA-Z0-9]{4}\s[0-9]{4}\s[0-9]{4}\s[0-9]{4}\s[0-9]{2}", RegexOptions.Compiled);
                MatchCollection match = rx.Matches(annotation.Text);


                string[] splitText = annotation.Text.Split(new[] { "\r\n", "\r", "\n" },
                                                            StringSplitOptions.None);
                List<string> billText = new List<string>();
                bool foundBill = false;
                for(int i = 0; i < splitText.Length; i++)
                {
                    if (foundBill)
                    {
                        billText.Add(splitText[i]);
                    }
                    if(splitText[i].Contains("- - - - - - - - - - - - -"))
                    {
                        foundBill = true;
                    }
                    
                }

                string buffer = "";
                foreach(string s in billText)
                {
                    buffer += s + "\n";
                }
                
                List<Tuple<string, string>> info = new List<Tuple<string, string>>();
                if (match.Count > 0)
                {
                    StringBuilder numero = new StringBuilder(match[0].Value);
                    numero[1] = 'I';
                    info.Add(new Tuple<string, string>("tilinumero", numero.ToString()));
                }

                for (int i = 0; i < billText.Count; i++)
                {
                    if (billText[i] == "Mottagare")
                        info.Add(new Tuple<string, string>("Saaja", billText[i + 1] + " " + billText[i + 2] + " " + billText[i + 3]));
                    else if (billText[i].Contains("Ref. nr"))
                        info.Add(new Tuple<string, string>("ViiteNumero", billText[i + 1]));
                }
                rx = new Regex(@"[0-9]{1,2}\.[0-9]{1,2}\.[0-9]{2,4}", RegexOptions.Compiled);
                match = rx.Matches(buffer);
                info.Add(new Tuple<string, string>("Eräpäivä ", match[0].Value));
                rx = new Regex(@"[0-9]+,[0-9]{2}", RegexOptions.Compiled);
                match = rx.Matches(buffer);
                info.Add(new Tuple<string, string>("Summa", match[0].Value));

                billText.Clear();
                for(int i = 0; i < info.Count; i++)
                {
                    billText.Add(info[i].Item1 + " " + info[i].Item2);
                }

                File.WriteAllLines("Lasku.txt", billText.ToArray());
            }
            else
            {


                Image img = Image.FromFile(args[0]);

                IReadOnlyList<EntityAnnotation> textAnnotations = client.DetectText(img);
                string textBlock = "";
                foreach (var text in textAnnotations)
                {
                    Console.WriteLine($"Description: {text.Description}");
                    textBlock += text.Description + " ";
                }


                TranslationClient transClient = TranslationClient.Create();
                var transresponse = transClient.TranslateText(
                    textBlock, "en", "fi");
                Console.WriteLine(transresponse.TranslatedText);
                var Langclient = LanguageServiceClient.Create();
                var response = Langclient.AnalyzeEntities(new Document()
                {
                    Content = transresponse.TranslatedText,
                    Type = Document.Types.Type.PlainText,
                    Language = "en"
                });
                foreach (var entity in response.Entities)
                {
                    Console.WriteLine($"\tName: {entity.Name}");
                    Console.WriteLine($"\tType: {entity.Type}");
                    Console.WriteLine($"\tSalience: {entity.Salience}");
                    Console.WriteLine("\tMentions:");
                    foreach (var mention in entity.Mentions)
                        Console.WriteLine($"\t\t{mention.Text.BeginOffset}: {mention.Text.Content}");
                    Console.WriteLine("\tMetadata:");
                    foreach (var keyval in entity.Metadata)
                    {
                        Console.WriteLine($"\t\t{keyval.Key}: {keyval.Value}");
                    }
                }
            }
        }

       
    }
}
