<p align="center">
  <strong>dwrean.net</strong>
</p>

<h1 align="center">dwrean Ελληνική Τηλεόραση</h1>

<p align="center">
  Δωρεάν portable IPTV εφαρμογή για ελληνικά τηλεοπτικά κανάλια σε Windows.
</p>

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-blue)
![Portable](https://img.shields.io/badge/Portable-Yes-green)
![.NET](https://img.shields.io/badge/.NET-8-purple)

## Τι είναι

Το **dwrean Ελληνική Τηλεόραση** είναι μια απλή portable εφαρμογή για Windows που συγκεντρώνει δωρεάν διαθέσιμα ελληνικά τηλεοπτικά streams σε ένα εύχρηστο περιβάλλον.

Η λίστα καναλιών διαβάζεται online από το δημόσιο project [Free-TV/IPTV](https://github.com/Free-TV/IPTV), ώστε αλλαγές σε κανάλια και stream URLs να μπορούν να εμφανίζονται χωρίς νέα έκδοση της εφαρμογής.

## Χαρακτηριστικά

- Αυτόματη ενημέρωση της ελληνικής λίστας καναλιών
- Τοπικό cache της τελευταίας επιτυχημένης λίστας
- Κατηγορίες καναλιών
- Αναζήτηση
- Αγαπημένα
- Αποθήκευση τελευταίου καναλιού
- Player βασισμένος σε LibVLC
- Υποστήριξη HLS και MPEG-DASH
- Πλήρης οθόνη
- Έλεγχος έντασης και mute
- Portable λειτουργία χωρίς εγκατάσταση
- Windows 10/11 64-bit

## Πηγή καναλιών

Η εφαρμογή δεν φιλοξενεί τηλεοπτικό περιεχόμενο και δεν αναμεταδίδει streams. Διαβάζει συνδέσμους προς δωρεάν διαθέσιμα streams από:

https://github.com/Free-TV/IPTV/blob/master/lists/greece.md

Η διαθεσιμότητα κάθε καναλιού εξαρτάται από τον αντίστοιχο πάροχο/σταθμό. Ορισμένα streams ενδέχεται να έχουν γεωγραφικούς περιορισμούς.

## Build

Απαιτείται .NET 8 SDK.

```powershell
dotnet restore
dotnet publish src/DwreanTv/DwreanTv.csproj -c Release -r win-x64 --self-contained true -o publish
```

Το GitHub Actions workflow δημιουργεί αυτόματα ZIP portable build.

## Σχετικά

Δημιουργήθηκε για το [dwrean.net](https://www.dwrean.net/).

---

**dwrean Ελληνική Τηλεόραση — δωρεάν ελληνική τηλεόραση για Windows**
