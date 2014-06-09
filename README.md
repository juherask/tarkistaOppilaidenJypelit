# Tarkista Oppilaiden Jypelit #

Tämä Jypeli-kirjastoa käyttävä ohjelma lataa pelit versionhallinnasta (snv.exe) ja tarkistaa ne (msbuild.exe). Ohjelman käyttötarkoitus on oppilaiden Nuorten Peliohjelmointikurssilla tekemien pelien nouto ja validiteetin tarkistaminen ennen parhaan pelin arviointia.

## Käyttöohje ##

Tarkistustehtävä määritellään suoraan lähdekoodiin. Ulkoisten ohjelmien polut ja tarkastustehtävä on kovakoodattu TarkistaOppilaidenPelit.cs -tiedostoon:

  string SVN_CLI_EXE = @"C:\Temp\svn-win32-1.8.8\bin\svn.exe";
  string MSBUILD_EXE = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MsBuild.exe";
  
Haettavat pelit määritellään GetHardCodedList()-aliohjelmaan. Katso esimerkki koodista.

## TODO ##
* Testaa