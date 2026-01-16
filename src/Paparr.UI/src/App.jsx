import { useState, useEffect } from 'react'
import axios from 'axios'
import './App.css'
import ImportQueue from './pages/ImportQueue'
import ImportHistory from './pages/ImportHistory'

function App() {
  const [currentPage, setCurrentPage] = useState('queue')

  const apiClient = axios.create({
    baseURL: '/api',
  })

  return (
    <div className="app">
      <nav className="navbar">
        <div className="navbar-brand">
          <h1>Paparr</h1>
          <p className="subtitle">Ebook Ingestion Service</p>
        </div>
        <ul className="navbar-menu">
          <li>
            <button
              className={`nav-btn ${currentPage === 'queue' ? 'active' : ''}`}
              onClick={() => setCurrentPage('queue')}
            >
              Import Queue
            </button>
          </li>
          <li>
            <button
              className={`nav-btn ${currentPage === 'history' ? 'active' : ''}`}
              onClick={() => setCurrentPage('history')}
            >
              Import History
            </button>
          </li>
        </ul>
      </nav>

      <main className="main-content">
        {currentPage === 'queue' && <ImportQueue apiClient={apiClient} />}
        {currentPage === 'history' && <ImportHistory apiClient={apiClient} />}
      </main>
    </div>
  )
}

export default App
